using System.Data;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlashSkink.Tests.Metadata;

/// <summary>
/// Unit tests for <see cref="BrainAccess"/>, <see cref="IBrainAccess"/>, and
/// <see cref="BrainScope"/>. Verifies the serialisation gate contract, cancellation,
/// dispose-waits-in-flight, idempotency, and exception-in-body gate release.
/// </summary>
public sealed class BrainAccessTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a raw open in-memory connection whose disposal is owned by the test.
    /// Used only when the test <em>does not</em> pass ownership to <see cref="BrainAccess"/>.
    /// </summary>
    private static SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    // ── LockAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LockAsync_Acquired_ReturnsScope_WithConnection()
    {
        // BrainAccess takes ownership of the connection (no using on conn).
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);

        using var scope = await brain.LockAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Open, scope.Connection.State);
        Assert.Same(conn, scope.Connection);
    }

    [Fact]
    public async Task LockAsync_TwoCallers_Serialise()
    {
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);

        // Signal channels.
        var taskAHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskARelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Task A: acquire the scope and hold it until signalled.
        var taskA = Task.Run(async () =>
        {
            using var scope = await brain.LockAsync(CancellationToken.None);
            taskAHolding.SetResult();
            await taskARelease.Task;
        });

        // Wait for Task A to acquire scope.
        await taskAHolding.Task;

        // Task B: attempt to acquire — must block while A holds.
        var taskB = Task.Run(async () =>
        {
            taskBStarted.SetResult();
            using var scope = await brain.LockAsync(CancellationToken.None);
        });

        await taskBStarted.Task;

        // Give Task B enough wall time to attempt WaitAsync and confirm it's blocked.
        await Task.Delay(80);
        Assert.False(taskB.IsCompleted,
            "Task B must not complete before Task A releases the scope.");

        // Release A — B must now be able to proceed.
        taskARelease.SetResult();
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(taskA.IsCompletedSuccessfully);
        Assert.True(taskB.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LockAsync_CancellationBeforeAcquire_ThrowsOce()
    {
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Already-cancelled token — OCE (or a subtype such as TaskCanceledException)
        // must be thrown before any gate wait. ThrowsAnyAsync accepts derived types.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => brain.LockAsync(cts.Token).AsTask());

        // Gate must still be healthy: a new lock must succeed.
        using var scope = await brain.LockAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Open, scope.Connection.State);
    }

    [Fact]
    public async Task LockAsync_CancellationWhileWaiting_ThrowsOce()
    {
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);

        var taskAHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskARelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Task A: acquire and hold.
        var taskA = Task.Run(async () =>
        {
            using var scope = await brain.LockAsync(CancellationToken.None);
            taskAHolding.SetResult();
            await taskARelease.Task;
        });
        await taskAHolding.Task;

        // Task B: await LockAsync with a token we will cancel mid-wait.
        using var cts = new CancellationTokenSource();
        var taskB = Task.Run(() => brain.LockAsync(cts.Token).AsTask());

        await Task.Delay(50); // give B time to enter WaitAsync
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);

        // Release A and confirm the gate is healthy afterwards.
        taskARelease.SetResult();
        await taskA;

        {
            using var scope = await brain.LockAsync(CancellationToken.None);
            Assert.Equal(ConnectionState.Open, scope.Connection.State);
        } // scope disposed here — gate returns to count=1
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_AfterScopeDisposed_DisposesConnection()
    {
        var conn = OpenConnection();
        var brain = new BrainAccess(conn);

        // Acquire a scope and dispose it, then dispose the BrainAccess.
        var scope = await brain.LockAsync(CancellationToken.None);
        scope.Dispose();

        await brain.DisposeAsync();

        // Connection must be closed (disposed) after BrainAccess.DisposeAsync.
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact]
    public async Task Dispose_WhileScopeHeld_BlocksUntilReleased()
    {
        var conn = OpenConnection();
        var brain = new BrainAccess(conn);

        var scopeAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeReleaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Task A: acquire a scope and hold it until asked to release.
        var taskA = Task.Run(async () =>
        {
            var scope = await brain.LockAsync(CancellationToken.None);
            scopeAcquired.SetResult(); // signal: A holds the scope
            await scopeReleaseGate.Task; // hold until signalled
            scope.Dispose();
        });

        await scopeAcquired.Task;

        // Start DisposeAsync in Task B — must block waiting for A to release.
        var disposeTask = Task.Run(() => brain.DisposeAsync().AsTask());

        // Give DisposeAsync time to start, then verify it's still waiting.
        await Task.Delay(100);
        Assert.False(disposeTask.IsCompleted,
            "DisposeAsync must be blocked while Task A holds the brain scope (Principle 17 — " +
            "dispose is compensation; waits for in-flight SQL to finish).");

        // Signal A to release; dispose must now complete within budget.
        scopeReleaseGate.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.Closed, conn.State);
        await taskA;
    }

    [Fact]
    public async Task LockAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var conn = OpenConnection();
        var brain = new BrainAccess(conn);
        await brain.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => brain.LockAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DoubleDispose_IsSafe()
    {
        var conn = OpenConnection();
        var brain = new BrainAccess(conn);

        await brain.DisposeAsync();

        // Second dispose must be a no-op — no throw, no double-release.
        var ex = await Record.ExceptionAsync(() => brain.DisposeAsync().AsTask());

        Assert.Null(ex);
    }

    // ── BrainScope dispose semantics ─────────────────────────────────────────

    [Fact]
    public void BrainScope_Default_DisposeIsNoOp()
    {
        // A default-valued (zero-initialised) BrainScope has a null gate. Dispose must
        // not throw — the pattern is used in WritePipeline.CommitBrainAsync's
        // try/finally for the "scope not yet acquired" path.
        var scope = default(BrainScope);

        var ex = Record.Exception(() => scope.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public async Task BrainScope_DoubleDispose_OnNonDefault_IsIdempotent()
    {
        // Contract: BrainScope holds a single-shot release token. The first Dispose
        // releases the gate; every later Dispose (e.g. on an accidental shallow copy
        // of the struct passed across a method boundary) is a safe no-op. Without
        // this, a second Dispose would call SemaphoreSlim.Release() on a maxCount=1
        // semaphore and throw SemaphoreFullException, corrupting the gate.
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);
        var scope = await brain.LockAsync(CancellationToken.None);

        scope.Dispose();

        var ex = Record.Exception(() => scope.Dispose());
        Assert.Null(ex);

        // Gate must still be healthy: a fresh acquire succeeds.
        using var scope2 = await brain.LockAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Open, scope2.Connection.State);
    }

    [Fact]
    public async Task BrainScope_CopyDoubleDispose_IsIdempotent()
    {
        // Accidental shallow copy across a method boundary (e.g. void DoWork(BrainScope)) —
        // the original and the copy share the same release token, so disposing both must
        // not double-release the gate.
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);
        var original = await brain.LockAsync(CancellationToken.None);
        var copy = original; // shallow copy of readonly struct

        original.Dispose();

        var ex = Record.Exception(() => copy.Dispose());
        Assert.Null(ex);

        // Gate must still be healthy.
        using var scope2 = await brain.LockAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Open, scope2.Connection.State);
    }

    // ── DisposeAsync race with in-flight LockAsync waiter ─────────────────────

    [Fact]
    public async Task LockAsync_WaiterRacingConcurrentDispose_ObservesObjectDisposed()
    {
        // Race: a thread calls LockAsync, passes the initial _disposed check, and
        // blocks inside _gate.WaitAsync. A second thread calls DisposeAsync, flips
        // _disposed, waits for the gate, disposes the connection, then releases the
        // gate. Without a re-verify check inside LockAsync, the waiter would wake on
        // the released gate and hand back a BrainScope wrapping a destroyed
        // SqliteConnection. The contract: the waiter must throw
        // ObjectDisposedException rather than return a corrupt scope.
        var conn = OpenConnection();
        var brain = new BrainAccess(conn);

        var holderAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Holder: acquire the gate so the waiter blocks inside WaitAsync.
        var holder = Task.Run(async () =>
        {
            using var scope = await brain.LockAsync(CancellationToken.None);
            holderAcquired.SetResult();
            await holderRelease.Task;
        });
        await holderAcquired.Task;

        // Waiter: enter LockAsync — will block on the gate behind the holder.
        var waiter = Task.Run(() => brain.LockAsync(CancellationToken.None).AsTask());
        await Task.Delay(50); // give waiter time to enter WaitAsync

        // Disposer: start dispose. It flips _disposed (so the waiter's re-verify
        // check will trip) and then queues behind the waiter on the gate.
        var disposer = Task.Run(() => brain.DisposeAsync().AsTask());
        await Task.Delay(50); // give dispose time to flip _disposed and start its WaitAsync

        // Release the holder. SemaphoreSlim is FIFO: the waiter wakes first, its
        // re-verify check trips on _disposed=1, it releases the gate and throws
        // ObjectDisposedException. The disposer then acquires the gate and finishes.
        holderRelease.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiter);
        await disposer.WaitAsync(TimeSpan.FromSeconds(5));
        await holder;

        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    // ── BrainScope gate-release on exception ─────────────────────────────────

    [Fact]
    public async Task ScopeDispose_OnExceptionInBody_StillReleasesGate()
    {
        var conn = OpenConnection();
        await using var brain = new BrainAccess(conn);

        // Throw from inside the scope body — the using statement must still call Dispose.
        try
        {
            using var scope = await brain.LockAsync(CancellationToken.None);
            throw new InvalidOperationException("test exception inside scope body");
        }
        catch (InvalidOperationException) { /* expected */ }

        // Gate must have been released. A subsequent LockAsync must succeed without
        // deadlocking or throwing.
        using var scope2 = await brain.LockAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Open, scope2.Connection.State);
    }
}
