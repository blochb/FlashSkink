using System.Collections.Concurrent;
using Dapper;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Orchestration;
using FlashSkink.Core.Providers;
using FlashSkink.Tests.Engine;
using FlashSkink.Tests.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.CrashConsistency;

/// <summary>
/// Concurrency regression tests for <see cref="BrainAccess"/> and the full volume lifecycle.
/// These are the direct successors of the spike in <c>docs/spike-findings.md</c>
/// § "2026-05-20 — SQLite dispose-time NRE on Windows CI" and the disconfirmed
/// <c>fix/upload-queue-dispose-race</c> candidate.
///
/// <para>
/// <strong>Counterpart.</strong> The spike proved that a raw <see cref="SqliteConnection"/>
/// fails with NRE under concurrent SQL at ~4.2% per 500-iteration run. The tests below
/// prove that wrapping the same workload in <see cref="BrainAccess"/> reduces that failure
/// rate to zero.
/// </para>
/// </summary>
public sealed class BrainAccessConcurrencyTests
{
    // ── Test 1: direct gate stress ────────────────────────────────────────────

    /// <summary>
    /// Runs 500 iterations of 20 concurrently-launched SQL tasks through a single
    /// <see cref="BrainAccess"/>. Each task acquires the brain scope, executes
    /// <c>SELECT COUNT(*) FROM SchemaVersions</c>, and releases. Asserts that no
    /// exception escapes across all 10 000 operations.
    ///
    /// <para>
    /// This is the direct counter-test to the spike's
    /// <c>SqliteConcurrentAccessSpike.TwoTasks_ConcurrentSql_OnOneConnection</c>
    /// which produced NRE at ~4.2% on a raw <see cref="SqliteConnection"/>. The
    /// gate serialises the SQL, eliminating the corruption path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentSql_ThroughBrainAccess_NoFailures()
    {
        const int Iterations = 500;
        const int Concurrency = 20;

        // BrainAccess owns the connection — no using on conn.
        var conn = BrainTestHelper.CreateInMemoryConnection();
        await BrainTestHelper.ApplySchemaAsync(conn);
        var brain = new BrainAccess(conn);

        var exceptions = new ConcurrentBag<Exception>();

        for (int i = 0; i < Iterations; i++)
        {
            // Launch Concurrency tasks that all compete for the gate simultaneously.
            var tasks = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(async () =>
            {
                try
                {
                    using var scope = await brain.LockAsync(CancellationToken.None);
                    // Real SQL — same query the spike used on a raw connection.
                    var count = await scope.Connection.QuerySingleAsync<int>(
                        "SELECT COUNT(*) FROM SchemaVersions");
                    _ = count; // consume result to prevent dead-code elimination
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();

            await Task.WhenAll(tasks);
        }

        // The gate serialises all SQL; Close() is guarded by DisposeAsync's WaitAsync.
        await brain.DisposeAsync();

        Assert.Empty(exceptions);
    }

    // ── Test 2: full volume lifecycle stress ──────────────────────────────────

    /// <summary>
    /// Runs 100 iterations of the exact lifecycle that triggered the original CI flake
    /// (<c>RegisterTail_IdempotentOnDuplicate</c>, run 26158829111): creates a real
    /// <see cref="FlashSkinkVolume"/> with background services running, calls
    /// <see cref="FlashSkinkVolume.RegisterTailAsync"/> twice for the same provider ID
    /// (the idempotent path), then calls <see cref="FlashSkinkVolume.DisposeAsync"/>.
    ///
    /// <para>
    /// The original NRE was a <see cref="NullReferenceException"/> inside
    /// <c>SqliteConnection.Close()</c> caused by a concurrent SQL operation (upload-queue
    /// scanner or brain mirror) on the shared <c>_brainConnection</c> racing the disposal
    /// path. With <see cref="BrainAccess"/>, <c>DisposeAsync</c> acquires the gate before
    /// closing the connection, eliminating the race.
    /// </para>
    ///
    /// <para>
    /// Each iteration uses its own temporary directory; the directory is deleted in a
    /// finally block. <see cref="SqliteConnection.ClearAllPools"/> is called at the end
    /// of every iteration to ensure Windows WAL handles are released before the next
    /// iteration creates a new volume at a fresh path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task VolumeLifecycle_StressRegisterTail_NoNre()
    {
        const int Iterations = 100;
        const string Password = "stress-test-password";
        const string ProviderId = "stress-tail-1";
        const string ProviderType = "filesystem";
        const string DisplayName = "Stress Tail 1";

        for (int i = 0; i < Iterations; i++)
        {
            var skinkRoot = Path.Combine(
                Path.GetTempPath(),
                "flashskink-brain-stress",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(skinkRoot);

            var bus = new RecordingNotificationBus();

            try
            {
                var options = new VolumeCreationOptions
                {
                    LoggerFactory = NullLoggerFactory.Instance,
                    NotificationBus = bus,
                };

                var createResult = await FlashSkinkVolume.CreateAsync(
                    skinkRoot, Password, options, CancellationToken.None);

                Assert.True(createResult.Success,
                    $"Iteration {i}: CreateAsync failed: {createResult.Error?.Message}");

                // Dispose the phrase immediately — this test doesn't need it.
                createResult.Value!.RecoveryPhrase.Dispose();

                // DisposeAsync on the volume is called via await using.
                await using var volume = createResult.Value!.Volume;

                // Create a tail root directory for the FileSystem provider.
                var tailRoot = Path.Combine(skinkRoot, "tail");
                Directory.CreateDirectory(tailRoot);
                var provider = new FileSystemProvider(
                    ProviderId, DisplayName, tailRoot,
                    NullLogger<FileSystemProvider>.Instance);

                // First registration — inserts the Providers row.
                var first = await volume.RegisterTailAsync(
                    ProviderId, ProviderType, DisplayName, null, provider,
                    CancellationToken.None);

                Assert.True(first.Success,
                    $"Iteration {i}: first RegisterTailAsync failed: {first.Error?.Message}");

                // Second registration with the same ID — exercises the idempotent path
                // (SELECT finds the existing row; no INSERT is issued). This matches the
                // exact sequence in the original CI-flaky test.
                var second = await volume.RegisterTailAsync(
                    ProviderId, ProviderType, DisplayName, null, provider,
                    CancellationToken.None);

                Assert.True(second.Success,
                    $"Iteration {i}: second RegisterTailAsync failed: {second.Error?.Message}");

                // DisposeAsync runs here (via await using) — this is where the NRE
                // surfaced before the fix: the background services raced the Close().
            }
            finally
            {
                // Clear WAL pools before the next iteration creates a new volume.
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(skinkRoot, recursive: true); } catch { /* best-effort */ }
            }

            // Verify no Error/Critical notifications escaped from the volume lifecycle.
            // This check is done after the finally so it still runs even if cleanup threw.
            var errors = bus.Published
                .Where(n => n.Severity is NotificationSeverity.Error or NotificationSeverity.Critical)
                .Select(n => $"[{n.Severity}] {n.Source}: {n.Title} — {n.Message}")
                .ToList();

            Assert.Empty(errors);
        }
    }
}
