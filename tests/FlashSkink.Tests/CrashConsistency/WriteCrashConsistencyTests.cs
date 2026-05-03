using Dapper;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using FlashSkink.Tests.Metadata;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
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
}
