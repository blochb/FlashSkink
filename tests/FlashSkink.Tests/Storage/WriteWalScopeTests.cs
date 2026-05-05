using System.Text.Json;
using Dapper;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Storage;

public sealed class WriteWalScopeTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WalRepository _wal;
    private readonly AtomicBlobWriter _blobWriter;
    private readonly string _skinkRoot;

    public WriteWalScopeTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _wal = new WalRepository(_connection, NullLogger<WalRepository>.Instance);
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-wal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_skinkRoot);
        _blobWriter = new AtomicBlobWriter(NullLogger<AtomicBlobWriter>.Instance);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_skinkRoot))
        {
            Directory.Delete(_skinkRoot, recursive: true);
        }
    }

    private static string MakeBlobId() => Guid.NewGuid().ToString("N");

    private static string MakeFileId() => Guid.NewGuid().ToString("N");

    private Task<Result<WriteWalScope>> OpenAsync(
        string? blobId = null, string? fileId = null, string? virtualPath = null)
    {
        return WriteWalScope.OpenAsync(
            _wal, _blobWriter, _skinkRoot,
            fileId ?? MakeFileId(),
            blobId ?? MakeBlobId(),
            virtualPath ?? "/test/file.bin",
            NullLogger<WriteWalScope>.Instance,
            CancellationToken.None);
    }

    private string QueryPhase(string walId) =>
        _connection.QuerySingle<string>(
            "SELECT Phase FROM WAL WHERE WALID = @WalId", new { WalId = walId });

    private string QueryWalId() =>
        _connection.QuerySingle<string>("SELECT WALID FROM WAL");

    // ── OpenAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_InsertsWalRow_InPreparePhase()
    {
        var blobId = MakeBlobId();
        var fileId = MakeFileId();
        const string virtualPath = "/docs/report.pdf";

        var result = await WriteWalScope.OpenAsync(
            _wal, _blobWriter, _skinkRoot, fileId, blobId, virtualPath,
            NullLogger<WriteWalScope>.Instance, CancellationToken.None);

        Assert.True(result.Success);
        await using var scope = result.Value!; // ! safe: Success asserted above
        var rows = await _wal.ListIncompleteAsync(CancellationToken.None);
        Assert.True(rows.Success);
        Assert.Single(rows.Value!);
        var row = rows.Value![0]; // ! safe: Single asserted above
        Assert.Equal("WRITE", row.Operation);
        Assert.Equal("PREPARE", row.Phase);
        // Deserialize the JSON payload to verify values; avoids Windows path backslash-escaping issues.
        var payload = JsonSerializer.Deserialize<JsonElement>(row.Payload);
        Assert.Equal(fileId, payload.GetProperty("FileID").GetString());
        Assert.Equal(blobId, payload.GetProperty("BlobID").GetString());
        Assert.Equal(virtualPath, payload.GetProperty("VirtualPath").GetString());
        Assert.Equal(_skinkRoot, payload.GetProperty("SkinkRoot").GetString());
    }

    [Fact]
    public async Task OpenAsync_WalInsertFails_ReturnsFailNoScope()
    {
        // Use a fresh connection without schema applied → WAL table doesn't exist → SqliteException.
        using var badConn = BrainTestHelper.CreateInMemoryConnection();
        var failingWal = new WalRepository(badConn, NullLogger<WalRepository>.Instance);

        var result = await WriteWalScope.OpenAsync(
            failingWal, _blobWriter, _skinkRoot, MakeFileId(), MakeBlobId(), "/path",
            NullLogger<WriteWalScope>.Instance, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DatabaseWriteFailed, result.Error!.Code);
        Assert.Null(result.Value);
    }

    // ── CompleteAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_TransitionsToCommitted()
    {
        var result = await OpenAsync();
        Assert.True(result.Success);
        await using var scope = result.Value!; // ! safe: Success asserted above
        var walId = QueryWalId();

        var completeResult = await scope.CompleteAsync();

        Assert.True(completeResult.Success);
        Assert.Equal("COMMITTED", QueryPhase(walId));
    }

    [Fact]
    public async Task CompleteAsync_CalledTwice_IsIdempotent()
    {
        var result = await OpenAsync();
        Assert.True(result.Success);
        await using var scope = result.Value!; // ! safe: Success asserted above
        var walId = QueryWalId();

        var first = await scope.CompleteAsync();
        var updatedAfterFirst = _connection.QuerySingle<string>(
            "SELECT UpdatedUtc FROM WAL WHERE WALID = @WalId", new { WalId = walId });

        var second = await scope.CompleteAsync();
        var updatedAfterSecond = _connection.QuerySingle<string>(
            "SELECT UpdatedUtc FROM WAL WHERE WALID = @WalId", new { WalId = walId });

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal("COMMITTED", QueryPhase(walId));
        // Second call is a no-op: UpdatedUtc unchanged (no DB round-trip).
        Assert.Equal(updatedAfterFirst, updatedAfterSecond);
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_AfterComplete_DoesNotTransitionToFailed()
    {
        var result = await OpenAsync();
        Assert.True(result.Success);
        var walId = QueryWalId();

        await using (var scope = result.Value!) // ! safe: Success asserted above
        {
            await scope.CompleteAsync();
        }

        // Dispose after complete should leave the row at COMMITTED.
        Assert.Equal("COMMITTED", QueryPhase(walId));
    }

    [Fact]
    public async Task DisposeAsync_WithoutComplete_TransitionsToFailedAndCleansStaging()
    {
        var blobId = MakeBlobId();
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!); // ! safe: stagingPath is always a file path with a parent dir
        File.WriteAllBytes(stagingPath, [0xAB, 0xCD]);

        var result = await OpenAsync(blobId: blobId);
        Assert.True(result.Success);
        var walId = QueryWalId();

        await using (result.Value!) { }  // ! safe: Success asserted above; dispose without complete

        Assert.Equal("FAILED", QueryPhase(walId));
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task DisposeAsync_WithMarkRenamed_AlsoDeletesDestination()
    {
        var blobId = MakeBlobId();
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!); // ! safe: stagingPath is always a file path with a parent dir
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!); // ! safe: destPath is always a file path with a parent dir
        File.WriteAllBytes(stagingPath, [0x01]);
        File.WriteAllBytes(destPath, [0x02]);

        var result = await OpenAsync(blobId: blobId);
        Assert.True(result.Success);
        var walId = QueryWalId();
        result.Value!.MarkRenamed(); // ! safe: Success asserted above

        await using (result.Value!) { }  // dispose without complete

        Assert.Equal("FAILED", QueryPhase(walId));
        Assert.False(File.Exists(stagingPath));
        Assert.False(File.Exists(destPath));
    }

    [Fact]
    public async Task DisposeAsync_WithoutMarkRenamed_LeavesDestinationUntouched()
    {
        var blobId = MakeBlobId();
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!); // ! safe: destPath is always a file path with a parent dir
        File.WriteAllBytes(destPath, [0x99]);  // simulate external file at dest (should not be touched)

        var result = await OpenAsync(blobId: blobId);
        Assert.True(result.Success);
        var walId = QueryWalId();
        // MarkRenamed NOT called.

        await using (result.Value!) { }  // ! safe: Success asserted above; dispose without complete

        Assert.Equal("FAILED", QueryPhase(walId));
        // Destination must still exist — MarkRenamed was never set.
        Assert.True(File.Exists(destPath));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var result = await OpenAsync();
        Assert.True(result.Success);
        var walId = QueryWalId();
        var scope = result.Value!; // ! safe: Success asserted above

        await scope.DisposeAsync();
        await scope.DisposeAsync();  // second dispose must be a no-op

        Assert.Equal("FAILED", QueryPhase(walId));
        // Row remains at FAILED, not double-transitioned.
        var count = _connection.QuerySingle<int>("SELECT COUNT(*) FROM WAL WHERE WALID = @WalId AND Phase = 'FAILED'",
            new { WalId = walId });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DisposeAsync_WalTransitionFails_LogsErrorAndDoesNotThrow()
    {
        var logger = new RecordingLogger<WriteWalScope>();
        var result = await WriteWalScope.OpenAsync(
            _wal, _blobWriter, _skinkRoot, MakeFileId(), MakeBlobId(), "/test",
            logger, CancellationToken.None);
        var walId = QueryWalId();

        // Close the connection so the WAL transition fails.
        _connection.Close();

        // DisposeAsync must swallow the failure — no exception escapes.
        var ex = await Record.ExceptionAsync(async () => await result.Value!.DisposeAsync()); // ! safe: opened successfully above

        Assert.Null(ex);
        Assert.True(logger.HasEntry(LogLevel.Error, walId),
            "Expected an Error log mentioning the WAL row ID.");
    }

    // ── CompleteAsync — transaction parameter (§2.5) ─────────────────────────

    [Fact]
    public async Task CompleteAsync_WithTransaction_TransitionParticipatesInTx()
    {
        // Open a scope to create the WAL row in PREPARE.
        var result = await OpenAsync();
        Assert.True(result.Success);
        await using var scope = result.Value!;
        var walId = QueryWalId();

        // Begin a transaction, complete the scope inside it, then roll back.
        using var tx = _connection.BeginTransaction();
        var completeResult = await scope.CompleteAsync(transaction: tx);
        Assert.True(completeResult.Success);
        tx.Rollback();

        // The rollback must undo the COMMITTED transition — row must still be in PREPARE.
        var phase = QueryPhase(walId);
        Assert.Equal("PREPARE", phase);
    }

    [Fact]
    public async Task ConfirmCommitted_AfterCompleteAsyncWithTransaction_DisposeAsync_IsNoOp()
    {
        var blobId = MakeBlobId();
        var result = await OpenAsync(blobId: blobId);
        Assert.True(result.Success);
        var walId = QueryWalId();

        using var tx = _connection.BeginTransaction();
        var completeResult = await result.Value!.CompleteAsync(transaction: tx);
        Assert.True(completeResult.Success);
        tx.Commit();
        result.Value!.ConfirmCommitted();

        await result.Value!.DisposeAsync();

        // Commit succeeded and ConfirmCommitted was called — DisposeAsync must not roll back.
        Assert.Equal("COMMITTED", QueryPhase(walId));
    }

    [Fact]
    public async Task CompleteAsync_WithTransaction_TxRolledBack_DisposeAsync_CleansUp()
    {
        // Simulates tx.Commit() throwing (e.g. SQLITE_IOERR) — SQLite rolls back the tx,
        // undoing the WAL COMMITTED UPDATE. ConfirmCommitted is never called.
        // DisposeAsync must still run cleanup because _completed was not set.
        var blobId = MakeBlobId();
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllBytes(stagingPath, [0x01]);
        File.WriteAllBytes(destPath, [0x02]);

        var result = await OpenAsync(blobId: blobId);
        Assert.True(result.Success);
        await using var scope = result.Value!;
        var walId = QueryWalId();
        scope.MarkRenamed();

        using var tx = _connection.BeginTransaction();
        var completeResult = await scope.CompleteAsync(transaction: tx);
        Assert.True(completeResult.Success);
        tx.Rollback(); // simulates commit failure — WAL UPDATE is undone; ConfirmCommitted not called

        // DisposeAsync runs via await using — must clean up because _completed was never set.
        await scope.DisposeAsync();

        Assert.Equal("FAILED", QueryPhase(walId));
        Assert.False(File.Exists(stagingPath));
        Assert.False(File.Exists(destPath));
    }

    // ── Principle 17 source-grep ──────────────────────────────────────────────

    [Fact]
    public void Compensation_UsesCancellationTokenNone_Literal()
    {
        var sourceFile = Path.Combine(
            RepoRoot.Path, "src", "FlashSkink.Core", "Storage", "WriteWalScope.cs");
        Assert.True(File.Exists(sourceFile),
            $"Source file not found at {sourceFile}. RepoRoot: {RepoRoot.Path}");

        var fullSource = File.ReadAllText(sourceFile);

        // For each compensation call pattern, find it in the source and verify that within the
        // same statement (up to the closing semicolon), CancellationToken.None also appears.
        string[] compensationPatterns =
        [
            ".DeleteStagingAsync(",
            ".DeleteDestinationAsync(",
            ".TransitionAsync(",
        ];

        foreach (var pattern in compensationPatterns)
        {
            var pos = 0;
            var found = false;
            while (true)
            {
                var idx = fullSource.IndexOf(pattern, pos, StringComparison.Ordinal);
                if (idx < 0)
                {
                    break;
                }
                found = true;
                var semiPos = fullSource.IndexOf(';', idx);
                var statement = semiPos >= 0 && semiPos - idx < 500
                    ? fullSource[idx..semiPos]
                    : fullSource[idx..Math.Min(idx + 500, fullSource.Length)];

                Assert.True(
                    statement.Contains("CancellationToken.None", StringComparison.Ordinal),
                    $"Compensation call '{pattern}' must pass CancellationToken.None as a literal " +
                    $"(Principle 17). Statement: {statement.Trim()}");

                pos = idx + 1;
            }

            Assert.True(found, $"Expected to find '{pattern}' in WriteWalScope.cs but it was absent.");
        }
    }
}
