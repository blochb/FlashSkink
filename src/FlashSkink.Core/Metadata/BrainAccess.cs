using Microsoft.Data.Sqlite;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Thread-safe access wrapper around the brain <see cref="SqliteConnection"/>.
/// Callers acquire a <see cref="BrainScope"/> via <see cref="LockAsync"/> before
/// issuing SQL; the scope releases the gate when disposed. Backed by a single
/// per-volume <see cref="SemaphoreSlim"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why.</strong> <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> is
/// not thread-safe — its internal <c>_commands</c> collection is an unsynchronized
/// <c>List&lt;T&gt;</c>. Two threads issuing SQL on one connection can corrupt the
/// list; the corruption later surfaces as <see cref="NullReferenceException"/>
/// inside <see cref="SqliteConnection.Close()"/>. See
/// <c>docs/spike-findings.md</c> § "2026-05-20 — SQLite dispose-time NRE on
/// Windows CI" for the reproducer and evidence.
/// </para>
/// <para>
/// <strong>Contract.</strong> Every component that issues SQL on the brain
/// receives an <see cref="IBrainAccess"/>, never a raw
/// <see cref="SqliteConnection"/>. The only sanctioned exception is the
/// volume-open path (<c>BrainConnectionFactory.CreateAsync</c> and
/// <c>MigrationRunner.RunAsync</c>) which runs before any background service
/// exists. Enforced by Principle 36 in <c>CLAUDE.md</c>.
/// </para>
/// <para>
/// <strong>Transactions.</strong> Repository methods that accept a
/// <see cref="SqliteTransaction"/> parameter do <em>not</em> acquire the gate —
/// the caller holds the scope for the entire transaction lifetime and the
/// repository SQL uses <see cref="SqliteTransaction.Connection"/>. This avoids
/// the need for reentrant gate semantics (which async-locals make fragile).
/// </para>
/// </remarks>
public interface IBrainAccess
{
    /// <summary>
    /// Acquires the brain serialisation gate and returns a scope exposing the
    /// underlying connection. Dispose the scope to release the gate. Throws
    /// <see cref="OperationCanceledException"/> if <paramref name="ct"/> cancels
    /// before the gate is acquired. Throws <see cref="ObjectDisposedException"/>
    /// after the owning <see cref="BrainAccess"/> has been disposed.
    /// </summary>
    ValueTask<BrainScope> LockAsync(CancellationToken ct);
}

/// <summary>
/// Disposable scope returned by <see cref="IBrainAccess.LockAsync"/>. Exposes
/// the brain <see cref="SqliteConnection"/> for SQL operations and releases the
/// gate on <see cref="Dispose"/>. Use with <c>using var scope = await
/// brain.LockAsync(ct);</c>.
/// </summary>
public readonly struct BrainScope : IDisposable
{
    private readonly SemaphoreSlim? _gate;

    /// <summary>The brain connection. Valid only for the lifetime of this scope.</summary>
    public SqliteConnection Connection { get; }

    internal BrainScope(SqliteConnection connection, SemaphoreSlim gate)
    {
        Connection = connection;
        _gate = gate;
    }

    /// <summary>
    /// Releases the brain gate. <strong>Not idempotent on a non-default scope</strong> — calling
    /// <see cref="Dispose"/> a second time would invoke <see cref="SemaphoreSlim.Release()"/>
    /// twice on a <c>maxCount</c>-1 semaphore and throw
    /// <see cref="SemaphoreFullException"/>. The <c>using var</c> convention used by every
    /// caller in this codebase guarantees a single dispose. Calling on a default-valued
    /// (zero-initialised) scope is a safe no-op.
    /// </summary>
    public void Dispose()
    {
        _gate?.Release();
    }
}

/// <summary>
/// Concrete <see cref="IBrainAccess"/>. Owns the lifetime of the brain
/// <see cref="SqliteConnection"/>: <see cref="DisposeAsync"/> waits for any
/// in-flight scope to release before closing the connection, eliminating the
/// dispose-vs-in-flight-SQL race described in
/// <c>docs/spike-findings.md</c> § "2026-05-20".
/// </summary>
public sealed class BrainAccess : IBrainAccess, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    /// <summary>
    /// Creates a <see cref="BrainAccess"/> taking ownership of
    /// <paramref name="connection"/>'s disposal. The connection must already be
    /// open and keyed.
    /// </summary>
    public BrainAccess(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <inheritdoc />
    public async ValueTask<BrainScope> LockAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BrainAccess));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new BrainScope(_connection, _gate);
    }

    /// <summary>
    /// Disposes the brain access and the underlying connection. Idempotent.
    /// Waits for any in-flight <see cref="BrainScope"/> to release before
    /// disposing the connection — Principle 17 (dispose is compensation; must
    /// not race in-flight operations). The wait itself is uncancellable.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Principle 17 — uncancellable wait so an in-flight scope can finish
        // its SQL and release before we Close() the connection. This is the
        // structural fix for the CI flake.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
