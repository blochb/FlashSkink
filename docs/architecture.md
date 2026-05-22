# Architecture

TBD — full derivation from `BLUEPRINT.md` will follow. The notes below cover the
brain-layer thread-safety contract added in `fix/brain-connection-thread-safety`.

## Brain access (Principle 36)

The brain database is an encrypted SQLCipher file at
`[skinkRoot]/.flashskink/brain.db`. A single long-lived `SqliteConnection`
is opened at volume create / open time by `BrainConnectionFactory` and lives
for the volume's lifetime. The brain key is derived once via Argon2 from the
DEK; the connection runs with `Pooling = false` so file handles release cleanly
on USB disconnect (Windows specifically — see `docs/spike-findings.md`
"Decisions deferred" for the alternatives we considered and rejected).

`Microsoft.Data.Sqlite.SqliteConnection` is **not thread-safe**. Its internal
`_commands` collection is an unsynchronized `List<T>`, and concurrent SQL on
one connection corrupts it. To enforce single-threaded access without
restructuring the volume into many connections, every brain-touching component
receives an `IBrainAccess` (`FlashSkink.Core.Metadata`) instead of a raw
`SqliteConnection`, and acquires a disposable `BrainScope` before any SQL:

```csharp
using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
await scope.Connection.ExecuteAsync(...);
```

The scope holds a single per-volume `SemaphoreSlim(1, 1)`; disposal releases
the gate. Multi-statement transactions hold one scope for the entire
transaction lifetime (open → all statements → commit). Repository overloads
that take a `SqliteTransaction` parameter assume the caller already holds
the scope and skip the acquisition — they run SQL against
`transaction.Connection`.

The sanctioned exceptions to "no raw connection downstream" are the volume-open
paths: `BrainConnectionFactory.CreateAsync` and `MigrationRunner.RunAsync` may
take a raw `SqliteConnection` because they run before any background service
exists and concurrent access is impossible.

`BrainAccess.DisposeAsync` waits for any in-flight scope to release before
closing the connection (under `CancellationToken.None` per Principle 17), which
eliminates the dispose-vs-in-flight-SQL race documented in
`docs/spike-findings.md` § "2026-05-20".
