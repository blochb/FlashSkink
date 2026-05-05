using System.Security.Cryptography;
using Dapper;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using FlashSkink.Tests.Metadata;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IO;
using Xunit;

namespace FlashSkink.Tests.CrashConsistency;

/// <summary>
/// FsCheck-driven property tests verifying that the §21.3 crash-consistency invariant
/// holds for the WRITE operation across all crash-at-step-N interleavings. Each iteration
/// models a crash at one of five points in the §13.4 write sequence, then runs
/// <see cref="WriteWalScope.DisposeAsync"/> as the recovery action and checks the invariant.
///
/// Crash steps modelled:
/// <list type="number">
///   <item>Before staging write — no files created.</item>
///   <item>After staging write, before fsync — staging file exists, no destination.</item>
///   <item>After staging fsync, before rename — same observable state as step 2.</item>
///   <item>After rename, before directory fsync — destination exists, staging gone, MarkRenamed NOT set.</item>
///   <item>After successful WriteAsync, before brain commit — destination exists, MarkRenamed IS set.</item>
/// </list>
/// </summary>
public sealed class WriteCrashConsistencyTests
{
    // ── FsCheck property ──────────────────────────────────────────────────────

    /// <summary>
    /// For any crash step in [1..5], after recovery (<see cref="WriteWalScope.DisposeAsync"/>),
    /// the §21.3 invariant holds: WAL row is FAILED, no orphan staging file, and no
    /// <c>Files</c> or <c>Blobs</c> rows exist (vacuously true — no brain commit happened in §2.4).
    /// </summary>
    [Property(MaxTest = 200)]
    public void WriteCrash_AtAnyStep_PreservesInvariant(PositiveInt crashAtStepBoxed)
    {
        var crashAtStep = ((crashAtStepBoxed.Get - 1) % 5) + 1; // ∈ [1..5]

        var iterRoot = Path.Combine(
            Path.GetTempPath(), "flashskink-crash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(iterRoot);
        try
        {
            RunScenario(crashAtStep, iterRoot);
        }
        finally
        {
            Directory.Delete(iterRoot, recursive: true);
        }
    }

    // ── Skipped power-off beacon ──────────────────────────────────────────────

    /// <summary>
    /// Power-off crash-injection beacon. Not a working test — see Skip message.
    /// </summary>
    [Fact(Skip =
        "Manual crash-injection test — requires VM snapshot or USB unplug. " +
        "Run before V1 ship to validate the §13.4 step-6 directory-fsync " +
        "assumption on Windows (AtomicBlobWriter.FsyncDirectory no-op). " +
        "If the rename does not survive power-loss, add FILE_FLAG_BACKUP_SEMANTICS " +
        "handle in AtomicBlobWriter.FsyncDirectory on Windows.")]
    public Task WriteAsync_PowerOff_AfterRenameBeforeBrainCommit_RenamesSurvives() =>
        throw new NotImplementedException("See attribute Skip message.");

    // ── Scenario runner ───────────────────────────────────────────────────────

    private static void RunScenario(int crashAtStep, string skinkRoot)
    {
        using var conn = BrainTestHelper.CreateInMemoryConnection();
        BrainTestHelper.ApplySchemaAsync(conn).GetAwaiter().GetResult();

        var wal = new WalRepository(conn, NullLogger<WalRepository>.Instance);
        var writer = new AtomicBlobWriter(NullLogger<AtomicBlobWriter>.Instance);
        var blobId = Guid.NewGuid().ToString("N");
        var fileId = Guid.NewGuid().ToString("N");
        byte[] testData = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];

        // Open WAL scope — inserts PREPARE row.
        var openResult = WriteWalScope.OpenAsync(
            wal, writer, skinkRoot, fileId, blobId, "/crash-test/file.bin",
            NullLogger<WriteWalScope>.Instance, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(openResult.Success, $"OpenAsync failed: {openResult.Error?.Message}");

        var scope = openResult.Value!;

        // Set up on-disk state to match what would exist at crash step N.
        SetUpDiskState(crashAtStep, writer, skinkRoot, blobId, scope, testData);

        // Act: dispose without CompleteAsync simulates the recovery path
        // (WAL recovery sweep calls DisposeAsync on incomplete scopes at startup).
        scope.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // ── Assert §21.3 invariant ────────────────────────────────────────────

        // 1. WAL row must be FAILED.
        var phase = conn.QuerySingle<string>(
            "SELECT Phase FROM WAL WHERE WALID = @WalId",
            new { WalId = GetWalId(conn) });
        Assert.Equal("FAILED", phase);

        // 2. No orphan staging file (the staging path must be clean after rollback).
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(skinkRoot, blobId);
        Assert.False(File.Exists(stagingPath),
            $"Orphan staging file found after rollback (crash step {crashAtStep}): {stagingPath}");

        // 2b. Destination file state varies by crash step:
        //   Step 4: MarkRenamed NOT called → DisposeAsync leaves the orphan for Phase-5 recovery.
        //   Steps 1-3, 5: destination either never existed (1-3) or MarkRenamed WAS called (5)
        //                 so DisposeAsync deleted it.
        var destPath = AtomicBlobWriter.ComputeDestinationPath(skinkRoot, blobId);
        if (crashAtStep == 4)
        {
            Assert.True(File.Exists(destPath),
                $"Step 4: destination orphan must survive rollback (Phase-5 recovers it): {destPath}");
        }
        else
        {
            Assert.False(File.Exists(destPath),
                $"Step {crashAtStep}: destination must not exist after rollback: {destPath}");
        }

        // 3. No Files or Blobs rows — no brain commit happened in this PR.
        //    The §2.5 PR extends this property test to include Files/Blobs seeding.
        var filesCount = conn.QuerySingle<int>("SELECT COUNT(*) FROM Files");
        var blobsCount = conn.QuerySingle<int>("SELECT COUNT(*) FROM Blobs");
        Assert.Equal(0, filesCount);
        Assert.Equal(0, blobsCount);
    }

    private static void SetUpDiskState(
        int crashAtStep,
        AtomicBlobWriter writer,
        string skinkRoot,
        string blobId,
        WriteWalScope scope,
        byte[] testData)
    {
        switch (crashAtStep)
        {
            case 1:
                // Crash before any write — nothing to create.
                break;

            case 2:
            case 3:
                // Crash after staging write (step 2: before fsync; step 3: after fsync, before rename).
                // Observable state is the same: staging file exists, no destination.
                var stagingPath = AtomicBlobWriter.ComputeStagingPath(skinkRoot, blobId);
                Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!); // ! safe: stagingPath is always a file path with a parent dir
                File.WriteAllBytes(stagingPath, testData);
                break;

            case 4:
                // Crash after rename, before directory fsync.
                // Model: WriteAsync completes fully (destination exists, staging gone),
                // but the process crashes before the §2.5 WritePipeline can call MarkRenamed().
                // MarkRenamed is NOT called, so DisposeAsync will not clean the destination.
                // The destination file remains as a recoverable orphan (Phase 5 handles it).
                var writeResult4 = writer.WriteAsync(skinkRoot, blobId, testData, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.True(writeResult4.Success, $"Step-4 WriteAsync failed: {writeResult4.Error?.Message}");
                // NOTE: MarkRenamed intentionally NOT called.
                break;

            case 5:
                // Crash after successful WriteAsync and MarkRenamed, before brain commit.
                // DisposeAsync will delete the destination and transition WAL to FAILED.
                var writeResult5 = writer.WriteAsync(skinkRoot, blobId, testData, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.True(writeResult5.Success, $"Step-5 WriteAsync failed: {writeResult5.Error?.Message}");
                scope.MarkRenamed();
                break;
        }
    }

    private static string GetWalId(SqliteConnection conn) =>
        conn.QuerySingle<string>("SELECT WALID FROM WAL LIMIT 1");

    // ── §2.5 extension: step 6 — brain INSERT INTO Files fails ───────────────

    /// <summary>
    /// Extends the §2.4 5-step crash model to 6 steps, adding step 6 = "brain INSERT INTO Files
    /// fails (UNIQUE constraint)". For steps 1–5 the existing <see cref="RunScenario"/> runner
    /// is reused unchanged. For step 6 a complete <see cref="WritePipeline.ExecuteAsync"/> run
    /// triggers the UNIQUE-constraint path and the §21.3 invariant is verified afterward:
    /// WAL row is FAILED, no orphan staging file, destination blob deleted by DisposeAsync,
    /// and the pre-existing Files + Blobs rows (from the seed write) remain intact on disk.
    /// </summary>
    [Property(MaxTest = 200)]
    public void WritePipelineCrash_AtAnyStep_PreservesInvariant(PositiveInt crashAtStepBoxed)
    {
        var crashAtStep = ((crashAtStepBoxed.Get - 1) % 6) + 1; // ∈ [1..6]

        var iterRoot = Path.Combine(
            Path.GetTempPath(), "flashskink-pipeline-crash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(iterRoot);
        try
        {
            if (crashAtStep <= 5)
            {
                // Steps 1–5: delegate to the §2.4 5-step WAL-recovery model unchanged.
                RunScenario(crashAtStep, iterRoot);
            }
            else
            {
                // Step 6: brain INSERT INTO Files fails via UNIQUE constraint after WriteAsync.
                RunPipelineBrainFailureScenario(iterRoot);
            }
        }
        finally
        {
            Directory.Delete(iterRoot, recursive: true);
        }
    }

    /// <summary>
    /// Simulates step 6: the on-disk write succeeds and MarkRenamed is set, but the brain
    /// transaction fails with a UNIQUE constraint on INSERT INTO Files (path conflict). Verifies
    /// that after the pipeline's internal rollback the §21.3 invariant holds: WAL row FAILED,
    /// no orphan staging file, new destination blob deleted, pre-existing rows intact.
    /// </summary>
    private static void RunPipelineBrainFailureScenario(string skinkRoot)
    {
        // Create required skink directory structure.
        Directory.CreateDirectory(Path.Combine(skinkRoot, ".flashskink", "staging"));
        Directory.CreateDirectory(Path.Combine(skinkRoot, ".flashskink", "blobs"));

        using var conn = BrainTestHelper.CreateInMemoryConnection();
        BrainTestHelper.ApplySchemaAsync(conn).GetAwaiter().GetResult();

        var loggerFactory = NullLoggerFactory.Instance;
        var wal = new WalRepository(conn, loggerFactory.CreateLogger<WalRepository>());
        var blobs = new BlobRepository(conn, loggerFactory.CreateLogger<BlobRepository>());
        var files = new FileRepository(conn, wal, loggerFactory.CreateLogger<FileRepository>());
        var activity = new ActivityLogRepository(conn, loggerFactory.CreateLogger<ActivityLogRepository>());

        var pipeline = new WritePipeline(
            new FileTypeService(),
            new EntropyDetector(),
            loggerFactory);

        byte[] dek = new byte[32]; // all-zeros DEK

        using var context = new VolumeContext(
            brainConnection: conn,
            dek: dek.AsMemory(),
            skinkRoot: skinkRoot,
            sha256: IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            crypto: new CryptoPipeline(),
            compression: new CompressionService(),
            blobWriter: new AtomicBlobWriter(loggerFactory.CreateLogger<AtomicBlobWriter>()),
            streamManager: new RecyclableMemoryStreamManager(),
            notificationBus: new NullNotificationBus(),
            blobs: blobs,
            files: files,
            wal: wal,
            activityLog: activity);

        // Root-level path — no folder creation needed; keeps the scenario focused on the
        // Files UNIQUE constraint path rather than folder-creation idempotency.
        const string virtualPath = "/crash-file.bin";

        // ── Seed: first write commits successfully ────────────────────────────
        var seedResult = pipeline.ExecuteAsync(
            new MemoryStream([0x01, 0x02, 0x03]), virtualPath, context, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.True(seedResult.Success, $"Seed write failed: {seedResult.Error?.Message}");
        string priorBlobId = seedResult.Value!.BlobId; // ! safe: Success asserted above
        string priorBlobPath = AtomicBlobWriter.ComputeDestinationPath(skinkRoot, priorBlobId);
        Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM Blobs"));
        Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 0"));
        Assert.True(File.Exists(priorBlobPath), "Pre-seeded blob must exist on disk.");

        // ── Crash: different content, same path → UNIQUE constraint on Files ──
        var crashResult = pipeline.ExecuteAsync(
            new MemoryStream([0xAA, 0xBB, 0xCC]), virtualPath, context, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.False(crashResult.Success, "Expected PathConflict failure.");
        Assert.Equal(ErrorCode.PathConflict, crashResult.Error!.Code); // ! safe: Success == false

        // ── Assert §21.3 invariant ────────────────────────────────────────────

        // 1. WAL row from the failed write is FAILED; no incomplete rows remain.
        Assert.Equal(1, conn.QuerySingle<int>(
            "SELECT COUNT(*) FROM WAL WHERE Phase = 'FAILED'"));
        Assert.Equal(0, conn.QuerySingle<int>(
            "SELECT COUNT(*) FROM WAL WHERE Phase NOT IN ('COMMITTED', 'FAILED')"));

        // 2. No orphan staging file.
        Assert.Empty(Directory.GetFiles(
            Path.Combine(skinkRoot, ".flashskink", "staging"), "*", SearchOption.AllDirectories));

        // 3. Brain transaction was rolled back — no new Blobs or Files rows committed.
        Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM Blobs"));
        Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 0"));

        // 4. Pre-seeded blob file survives the failed second write.
        Assert.True(File.Exists(priorBlobPath),
            "Pre-seeded blob file must survive the failed second write.");

        // 5. Blobs directory contains exactly one file — the destination blob written for the
        //    failed write was deleted by WriteWalScope.DisposeAsync (MarkRenamed was set before
        //    CommitBrainAsync failed).
        var blobDirFiles = Directory.GetFiles(
            Path.Combine(skinkRoot, ".flashskink", "blobs"), "*", SearchOption.AllDirectories);
        Assert.Single(blobDirFiles);

        // 6. Core §21.3 invariant: every committed Files row references an existing Blobs row
        //    whose on-disk blob file exists.
        var blobIdsFromFiles = conn.Query<string>(
            "SELECT BlobID FROM Files WHERE IsFolder = 0 AND BlobID IS NOT NULL").ToList();
        foreach (var blobId in blobIdsFromFiles)
        {
            var blobRowCount = conn.QuerySingle<int>(
                "SELECT COUNT(*) FROM Blobs WHERE BlobID = @BlobId", new { BlobId = blobId });
            Assert.Equal(1, blobRowCount);
            var blobFilePath = AtomicBlobWriter.ComputeDestinationPath(skinkRoot, blobId);
            Assert.True(File.Exists(blobFilePath),
                $"§21.3 invariant violated: BlobID={blobId} has no on-disk blob at {blobFilePath}");
        }
    }
}

/// <summary>No-op notification bus for crash-consistency test infrastructure.</summary>
file sealed class NullNotificationBus : INotificationBus
{
    public ValueTask PublishAsync(Notification notification, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
