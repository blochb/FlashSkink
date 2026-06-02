# PR 4.6a — Public tail-management API (`AddTailAsync` / `RemoveTailAsync` / `ListTailsAsync`)

**Branch:** `pr/4.6a-public-tail-api`
**Blueprint sections:** §11 (Core public API), §11.1 (behavioural notes — add queues every existing file; remove deletes token/queue/session rows), §16.2 (`Providers` / `TailUploads` schema)
**Dev plan section:** phase-4-providers §4.6 (first of a two-PR split: 4.6a = Core API; 4.6b = CLI + `skink` binary)
**Phase-4 cross-cutting decisions touched:** 1 (frozen contract), 2 (DEK envelope for tokens/secrets), 5 (`ProviderConstants`)

---

## Scope

Promote tail management to the public Core API and retire the §3.6 internal seam. No CLI in this PR.

1. **Delete `FlashSkinkVolume.RegisterTailAsync`** (internal, §3.6) and replace it with the three public §11 methods: `AddTailAsync(TailConfiguration, ct)`, `RemoveTailAsync(string providerId, ct)`, `ListTailsAsync(ct)`.
2. **`AddTailAsync` runs the full setup flow internally** — dispatches on `config.ProviderType` to the matching `IProviderSetup` (OAuth token exchange + adapter construction for cloud, or local-path validation), persists the DEK-encrypted credentials, registers the adapter in the live registry, writes the initial witness (§3.5.2), and backfills `TailUploads` for every existing file (§11.1 "queue every existing file").
3. **Add the mechanisms `AddTailAsync` needs**: a `TailConfiguration`/`TailInfo` DTO pair, a `VolumeCreationOptions.ProviderSetups` injection point, an `internal IMutableProviderRegistry` seam (implemented by both registries), and `_skinkRoot`/`_providerSetups` volume fields.

CLI commands, the `skink` binary rename, embedded guides, and the OAuth-capture wiring are **4.6b**.

---

## Why `AddTailAsync` runs the exchange internally (architecture note)

The dev-plan §4.6 scope and the earlier §4.1 plan note describe two different architectures; only one is viable. The CLI-does-exchange design **cannot work**: both `IProviderSetup.ExchangeCodeAsync` and `CreateProviderAsync` require the **DEK**, which lives in the `VolumeSession` and is never exposed to the CLI (Principle 31); and the client-secret envelope needs `ProviderTokenCrypto`, which is `internal` to Core. So `AddTailAsync` runs the exchange + construction (it has the DEK and `ProviderTokenCrypto`); the CLI (4.6b) does only the no-DEK parts and passes the single-use **authorization code** in. The refresh token never enters CLI memory — strictly better for Principles 6/26. The §4.1 note is treated as superseded.

---

## Design decisions (Gate 1)

- **D1 — `IProviderSetup` injection.** `AddTailAsync` dispatches on `config.ProviderType`. The volume holds a `Dictionary<string,IProviderSetup>` built in `BuildVolumeFromSessionAsync`: it **always** includes a Core-built `FileSystemProviderSetup` (which is `internal` — only Core can construct it), then overlays any setups from the new `VolumeCreationOptions.ProviderSetups`. Cloud setups are **not** eagerly built by Core (no per-open HttpClient cost); 4.6b's CLI injects them. Injected setups are **caller-owned** — the volume never disposes them.
- **D2 — Mutable-registry seam.** `IProviderRegistry` stays the frozen read-only seam (§3.6 Drift Note 2). New `internal interface IMutableProviderRegistry { void Register(string,IStorageProvider); bool Remove(string); }`, implemented by `InMemoryProviderRegistry` (already has both methods — add the interface) and `BrainBackedProviderRegistry` (add `Register` + `Remove`, where `Remove` disposes the evicted adapter). `AddTailAsync`/`RemoveTailAsync` require the registry to be `IMutableProviderRegistry`, else `Fail(InvalidArgument)` (generalises the old `RegisterTailAsync` `InMemoryProviderRegistry`-only guard so production `BrainBackedProviderRegistry` also qualifies).
- **D3 — `_skinkRoot` field.** `FileSystemProviderSetup.ValidatePathAsync` needs the skink root (backup-loop rejection). Add `private readonly string _skinkRoot`, set in the constructor (value already in scope in `BuildVolumeFromSessionAsync`).

---

## Files to create

- `src/FlashSkink.Core.Abstractions/Models/TailConfiguration.cs` — public sealed record; input to `AddTailAsync`. ~45 lines.
- `src/FlashSkink.Core.Abstractions/Models/TailInfo.cs` — public sealed record; output of `AddTailAsync`/`ListTailsAsync`. ~40 lines.
- `src/FlashSkink.Core/Providers/IMutableProviderRegistry.cs` — `internal interface` (D2). ~20 lines.
- `tests/FlashSkink.Tests/_TestSupport/FakeProviderSetup.cs` — canned `IProviderSetup` returning a caller-supplied `IStorageProvider`. ~90 lines.
- `tests/FlashSkink.Tests/Orchestration/AddTailAsyncTests.cs` — unit + integration for the three methods. ~470 lines.

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — delete `RegisterTailAsync`; add `AddTailAsync`/`RemoveTailAsync`/`ListTailsAsync`; add `_skinkRoot` + `_providerSetups` fields & ctor params; build the setup map in `BuildVolumeFromSessionAsync`. Net ~+260 / −145.
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs` — add `IReadOnlyList<IProviderSetup>? ProviderSetups`. ~+12.
- `src/FlashSkink.Core/Providers/InMemoryProviderRegistry.cs` — add `: IMutableProviderRegistry` (no method changes). ~+1.
- `src/FlashSkink.Core/Providers/BrainBackedProviderRegistry.cs` — add `: IMutableProviderRegistry`, `Register`, `Remove` (disposes the evicted adapter). ~+40.
- Existing test suites calling `RegisterTailAsync` (e.g. `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs`) — migrate to `AddTailAsync` (see "call-site migration").

## Dependencies

- **NuGet:** none new. **Project references:** none new.

---

## Public API surface

### `FlashSkink.Core.Abstractions.Models.TailConfiguration` (public sealed record)

Summary intent: describes a tail to add. Carries provider type + the credential material `AddTailAsync` needs. The plaintext `ClientSecret` lives here only during the setup call (cross-cutting decision 2); the refresh token never appears — `AddTailAsync` derives it from `AuthorizationCode` internally.

```csharp
public sealed record TailConfiguration
{
    /// <summary>Provider type token: "filesystem" | "google-drive" | "dropbox" | "onedrive".</summary>
    public required string ProviderType { get; init; }
    /// <summary>Stable provider id (brain PK). Null ⇒ AddTailAsync uses ProviderType (V1: one tail per type).</summary>
    public string? ProviderId { get; init; }
    /// <summary>Display name; null ⇒ AddTailAsync uses the setup's DisplayName.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Local folder for LocalPath providers (filesystem). Null for OAuth.</summary>
    public string? LocalPath { get; init; }
    /// <summary>BYOC OAuth client id (not secret). Required for OAuth providers.</summary>
    public string? ClientId { get; init; }
    /// <summary>BYOC OAuth client secret (plaintext). Required for OAuth; encrypted before persist.</summary>
    public string? ClientSecret { get; init; }
    /// <summary>OAuth authorization code captured via IOAuthCaptureFlow. Set ⇒ OAuth exchange runs.</summary>
    public string? AuthorizationCode { get; init; }
    /// <summary>PKCE verifier paired with the challenge used to obtain AuthorizationCode.</summary>
    public string? CodeVerifier { get; init; }
    /// <summary>Loopback redirect URI used during the OAuth dance.</summary>
    public string? RedirectUri { get; init; }
}
```

### `FlashSkink.Core.Abstractions.Models.TailInfo` (public sealed record)

```csharp
public sealed record TailInfo
{
    public required string ProviderId { get; init; }
    public required string ProviderType { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Last known health string from Providers.HealthStatus (e.g. "Healthy").</summary>
    public required string Health { get; init; }
    public required DateTimeOffset AddedUtc { get; init; }
    public DateTimeOffset? LastSuccessfulUploadUtc { get; init; }
    public long UploadedFileCount { get; init; }
    public long PendingFileCount { get; init; }
}
```

(Byte counts from Blueprint §11.1 are deferred — they need a `Blobs` join keyed on uploaded files. File counts answer "is my tail caught up?". Flagged for Gate 1: include uploaded-bytes now, or defer?)

### `FlashSkinkVolume` — three new public methods (replacing internal `RegisterTailAsync`)

```csharp
/// <summary>
/// Configures a new tail. Runs the matching IProviderSetup flow (OAuth token exchange + adapter
/// construction for cloud, or path validation for local folders), persists the DEK-encrypted
/// credentials to the brain, registers the adapter, writes the initial witness (§3.5.2), and queues
/// every existing file for upload to the new tail (§11.1). Returns immediately; uploads run in the
/// background.
/// </summary>
public Task<Result<TailInfo>> AddTailAsync(TailConfiguration config, CancellationToken ct = default);

/// <summary>
/// Removes a tail: deletes its UploadSessions, TailUploads, and Providers rows (one transaction),
/// then evicts the adapter from the live registry. Does NOT delete provider-side data.
/// </summary>
public Task<Result> RemoveTailAsync(string providerId, CancellationToken ct = default);

/// <summary>Returns one TailInfo per active Providers row, aggregating upload progress.</summary>
public Task<Result<IReadOnlyList<TailInfo>>> ListTailsAsync(CancellationToken ct = default);
```

### `VolumeCreationOptions.ProviderSetups` (new property)

```csharp
/// <summary>
/// IProviderSetup implementations available to AddTailAsync, overlaid on the Core-built
/// FileSystemProviderSetup (keyed by ProviderType). The CLI (4.6b) injects the cloud setups; tests
/// inject fakes. Injected setups are caller-owned (the volume does not dispose them). Null ⇒
/// filesystem-only.
/// </summary>
public IReadOnlyList<IProviderSetup>? ProviderSetups { get; init; }
```

---

## Internal types

### `IMutableProviderRegistry` (internal interface) — D2
```csharp
internal interface IMutableProviderRegistry
{
    void Register(string providerId, IStorageProvider provider);
    bool Remove(string providerId);
}
```
`InMemoryProviderRegistry` gains only `: IMutableProviderRegistry`. `BrainBackedProviderRegistry`: `Register` sets `_adapters[providerId] = provider`; `Remove` does `_adapters.TryRemove(providerId, out var evicted)`, then disposes `evicted` (prefer `IAsyncDisposable`, else `IDisposable`, literal `CancellationToken.None`, errors logged at Warning), returns whether one was removed.

---

## Method-body contracts

### `AddTailAsync(config, ct)`
1. `ThrowIfDisposed()`. Validate `config` non-null and `config.ProviderType` non-empty (else `Result<TailInfo>.Fail(InvalidArgument)`). `providerId = config.ProviderId ?? config.ProviderType`.
2. `setup = _providerSetups.GetValueOrDefault(config.ProviderType)`; null → `Fail(InvalidArgument,"Unknown provider type '…'.")`.
3. Registry must be `IMutableProviderRegistry mutable`; else `Fail(InvalidArgument)` (D2).
4. Acquire `_gate` (OCE → `Fail(Cancelled)`); `ThrowIfDisposedAndReleaseGate()`; then `try { … } finally { _gate.Release(); }`.
5. **Duplicate check** under brain scope: `SELECT ProviderID FROM Providers WHERE ProviderID=@providerId`. Present → `Fail(PathConflict,"A '{DisplayName}' tail is already configured.")` (one-tail-per-type; multi-account is a phase non-goal).
6. **Setup branch** on `setup.SetupKind`:
   - **LocalPath:** require `config.LocalPath`; `setup.ValidatePathAsync(config.LocalPath, _skinkRoot, ct)` — outer Fail propagate; `ValidationResult.IsValid==false` → `Fail(InvalidArgument, reason)`. Set `encryptedToken=null`, `encryptedSecret=null`, `clientId=null`, `providerConfigJson = {"rootPath":LocalPath}` (via `FileSystemProviderConfigJsonContext`).
   - **OAuth:** require `ClientId`,`ClientSecret`,`AuthorizationCode`,`CodeVerifier`,`RedirectUri` (any missing → `Fail(InvalidArgument)`). `credentials = new ProviderCredentials{ClientId,ClientSecret}`. `exch = await setup.ExchangeCodeAsync(code, verifier, redirectUri, credentials, _session.Dek, ct)` — Fail propagate; else `encryptedToken = exch.Value`. `encryptedSecret = ProviderTokenCrypto.Encrypt(config.ClientSecret, _session.Dek)`. `clientId = config.ClientId`. `providerConfigJson = null`. **Implementer: add an inline comment at this `null` line** — the resolved cloud folder id is not surfaced by `CreateProviderAsync`; `BrainBackedProviderRegistry` re-resolves it idempotently on the next open (`BrainBackedProviderRegistry.cs:388`); persisting it is a deferred post-V1 optimisation.
7. **Construct adapter:** `create = await setup.CreateProviderAsync(providerId, displayName, encryptedToken ?? [], credentials, providerConfigJson, _session.Dek, ct)` where `displayName = config.DisplayName ?? setup.DisplayName`, LocalPath `credentials = new ProviderCredentials()`. Fail propagate. Track `IStorageProvider? newAdapter = create.Value; bool registered = false;`.
8. **Persist + backfill** — one brain scope (Principle 36), one transaction:
   - `INSERT INTO Providers (ProviderID, ProviderType, DisplayName, EncryptedToken, EncryptedClientSecret, ClientId, ProviderConfig, HealthStatus, AddedUtc, IsActive) VALUES (…, 'Healthy', @now, 1)` (pass `null`, not `[]`, for empty token/secret).
   - **Backfill** `TailUploads` (§11.1), mirroring `WritePipeline.cs:560`:
     ```sql
     INSERT INTO TailUploads (FileID, ProviderID, Status, QueuedUtc, AttemptCount)
     SELECT FileID, @providerId, 'PENDING', @now, 0
     FROM Files
     WHERE IsFolder = 0 AND BlobID IS NOT NULL
       AND FileID NOT IN (SELECT FileID FROM TailUploads WHERE ProviderID=@providerId)
     ```
     Capture `pendingCount` = rows affected. Commit.
9. `mutable.Register(providerId, newAdapter)`; `registered = true`.
10. **Initial witness** — reuse the existing `RegisterTailAsync` witness block verbatim (read VolumeID/Epoch/State from `Settings`, `WitnessPayload.ForNewSession(...)`, `_witnessStore.WriteAsync(newAdapter, _session.Dek, payload, CancellationToken.None)`; failure logged at Warning, non-fatal).
11. `_wakeupSignal.Pulse()`.
12. Return `Result<TailInfo>.Ok(new TailInfo{ ProviderId, ProviderType, DisplayName, Health="Healthy", AddedUtc=now, LastSuccessfulUploadUtc=null, UploadedFileCount=0, PendingFileCount=pendingCount })`.

**Adapter-ownership / failure-disposal (Principle 16).** Ownership transfers to the registry only at step 9. Any exception after construction (step 7) but before `registered==true` (e.g. persist transaction fails, or `Register` throws) must dispose `newAdapter` exactly once before returning `Fail`: in **each** catch, `if (newAdapter is not null && !registered) { await DisposeAdapterAsync(newAdapter); }` (helper prefers `IAsyncDisposable`, falls back to `IDisposable`, literal `CancellationToken.None`, swallows+logs at Warning). Once `registered==true`, the registry owns disposal — `AddTailAsync` must **not** dispose (no double-dispose). The step-10 witness failure is non-fatal and returns `Ok`, so it never reaches a disposing catch.

**Secret-logging discipline (Principle 26).** No log template in `AddTailAsync` (or any setup adapter it calls) may format `TailConfiguration`/`ProviderCredentials`/auth code/client secret/refresh token — not even structured `{Config}` destructuring (the render leaks `ClientSecret`/`ClientId`). Log only non-secret scalars: `providerId`, `config.ProviderType`, `displayName`. Re-audit the §4.3–4.5 setup adapters for stray credential-formatting `Log*` calls on this path; narrow any found (permitted in-scope since it's on the `AddTailAsync` path).

Catch ordering (each doing the conditional disposal first): `OperationCanceledException`→`Cancelled`; `SqliteException when IsUniqueConstraintViolation()`→`PathConflict`; `SqliteException`→`DatabaseWriteFailed`; `Exception`→`Unknown`. No new `ErrorCode` values (verify names exist).

### `RemoveTailAsync(providerId, ct)`
1. `ThrowIfDisposed()`; validate `providerId`; registry must be `IMutableProviderRegistry`.
2. Gate (OCE→Cancelled); `ThrowIfDisposedAndReleaseGate()`.
3. Brain scope + transaction: confirm row exists (absent → `Fail(InvalidArgument,"No tail with that id is configured.")`); then `DELETE FROM UploadSessions WHERE ProviderID=@p`; `DELETE FROM TailUploads WHERE ProviderID=@p`; `DELETE FROM Providers WHERE ProviderID=@p`. Commit.
4. `mutable.Remove(providerId)` (evicts + disposes adapter).
5. `Result.Ok()`. Catch: OCE→Cancelled; SqliteException→DatabaseWriteFailed; Exception→Unknown.

### `ListTailsAsync(ct)`
1. `ThrowIfDisposed()`; gate (OCE→Cancelled); `ThrowIfDisposedAndReleaseGate()`.
2. Brain scope, single read (Dapper — admin path, Principle 22):
   ```sql
   SELECT p.ProviderID, p.ProviderType, p.DisplayName, p.HealthStatus, p.AddedUtc,
          (SELECT MAX(UploadedUtc) FROM TailUploads t WHERE t.ProviderID=p.ProviderID AND t.Status='UPLOADED') AS LastUploadUtc,
          (SELECT COUNT(*) FROM TailUploads t WHERE t.ProviderID=p.ProviderID AND t.Status='UPLOADED') AS UploadedCount,
          (SELECT COUNT(*) FROM TailUploads t WHERE t.ProviderID=p.ProviderID AND t.Status!='UPLOADED') AS PendingCount
   FROM Providers p WHERE p.IsActive=1 ORDER BY p.AddedUtc
   ```
3. Map to `TailInfo` (parse timestamps with `DateTimeOffset.Parse(..., CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)`; null `LastUploadUtc`→null). Catch: OCE→Cancelled; SqliteException→DatabaseReadFailed; Exception→Unknown.

### `BuildVolumeFromSessionAsync` (modification)
After the registry switch (§4.1), build the setup map:
```csharp
var setups = new Dictionary<string, IProviderSetup>(StringComparer.Ordinal)
{
    ["filesystem"] = new FileSystemProviderSetup(loggerFactory.CreateLogger<FileSystemProviderSetup>()),
};
if (options.ProviderSetups is not null)
{
    foreach (var s in options.ProviderSetups) { setups[s.ProviderType] = s; }
}
```
Pass `setups` + `skinkRoot` into the constructor; store as `_providerSetups`/`_skinkRoot`.

### Call-site migration (delete `RegisterTailAsync`)
Old tests passed a pre-built `IStorageProvider` to `RegisterTailAsync`. Migrate to `AddTailAsync` either via:
- the built-in `"filesystem"` setup: `AddTailAsync(new TailConfiguration{ProviderType="filesystem", LocalPath=_tailRoot})`, or
- a `FakeProviderSetup` whose `CreateProviderAsync` returns the test's pre-built provider, injected via `VolumeCreationOptions.ProviderSetups`, with `TailConfiguration{ProviderType="<fake>"}`.

**Registry caveat (Gate 1 feedback).** `AddTailAsync`'s `IMutableProviderRegistry` guard means any existing test that injected a hand-rolled / Moq `IProviderRegistry` (not `InMemoryProviderRegistry`) and then registered a tail will now hit the guard. Sweep for these: switch to a real `InMemoryProviderRegistry` (now `IMutableProviderRegistry`) + `FakeProviderSetup`. Read-only registry mocks (`GetAsync`/`ListActiveProviderIdsAsync` only) are unaffected.

---

## Integration points

- `IProviderSetup` (§4.1): `ProviderType`, `DisplayName`, `SetupKind`, `ExchangeCodeAsync`, `ValidatePathAsync`, `CreateProviderAsync` (`GetAuthorizationUriAsync` is a 4.6b/CLI concern).
- `ProviderTokenCrypto.Encrypt(string, ReadOnlyMemory<byte>)` (§4.1, internal) — client-secret envelope.
- `WitnessStore.WriteAsync(IStorageProvider, ReadOnlyMemory<byte>, WitnessPayload, CancellationToken)` + `WitnessPayload.ForNewSession(volumeId, epoch, appVersion)` (§3.5.2) — reused verbatim.
- `FileSystemProviderSetup(ILogger<FileSystemProviderSetup>)` (§4.1, internal).
- `FileSystemProviderConfig` + `FileSystemProviderConfigJsonContext` (existing) — rootPath JSON.
- Brain via `_context.Brain.LockAsync(ct)` → `BrainScope.Connection` (Principle 36).
- `WritePipeline.cs:560` TailUploads INSERT-SELECT — the backfill mirror.

---

## Principles touched
- **1** — public methods return `Result`/`Result<T>`.
- **2/3/4/5** — backfill fans every existing file to the new tail (mirror model); reads stay on the skink; Phase-1 commit untouched.
- **6/26** — refresh token never crosses the public boundary; client secret encrypted before persist; no credential object in any log template (see secret-logging discipline).
- **13/14/15/16** — `ct` last; OCE-first catch; specific-before-`Exception`; constructed adapter disposed in every catch only while `registered==false`, never after ownership transfer.
- **17** — witness write + adapter disposal use literal `CancellationToken.None`.
- **22** — `ListTailsAsync` uses Dapper (admin path).
- **23** — frozen provider contracts untouched; `IMutableProviderRegistry` is a *new internal* seam, not an edit to `IProviderRegistry`.
- **25** — Core error messages use skink/tail/folder vocabulary.
- **31/36** — DEK used transiently inside `AddTailAsync`, never stored on a long-lived field; all SQL via `IBrainAccess`.

---

## Test spec

### `_TestSupport/FakeProviderSetup.cs`
`internal sealed class FakeProviderSetup : IProviderSetup` — configurable `ProviderType`/`DisplayName`/`SetupKind`; `CreateProviderAsync` returns a caller-supplied `IStorageProvider`; `ExchangeCodeAsync` returns a caller-supplied `byte[]` (default a fixed envelope); `ValidatePathAsync`→`Valid`; `GetAuthorizationUriAsync`→fixed URI. Records call args.

### `Orchestration/AddTailAsyncTests.cs` — class `AddTailAsyncTests`
- `AddTail_FileSystem_ValidPath_InsertsRow_RegistersAdapter_ReturnsTailInfo`
- `AddTail_FileSystem_PathInsideSkink_ReturnsInvalidArgument`
- `AddTail_Oauth_RunsExchange_EncryptsSecret_PersistsClientIdAndToken` (assert `Providers` row has non-null `EncryptedToken`/`EncryptedClientSecret`/`ClientId`; decrypt secret with DEK and compare)
- `AddTail_BackfillsTailUploadsForExistingFiles` (write 3 files first; assert 3 PENDING rows + `PendingFileCount==3`)
- `AddTail_DuplicateProviderId_ReturnsPathConflict`
- `AddTail_UnknownProviderType_ReturnsInvalidArgument`
- `AddTail_WritesInitialWitnessToTail`
- `AddTail_NonMutableRegistry_ReturnsInvalidArgument`
- `AddTail_PersistFails_DisposesConstructedAdapter` (disposal-tracking adapter; force INSERT failure; assert `Disposed==true` + `Fail`)
- `AddTail_Success_DoesNotDisposeAdapter` (disposal-tracking adapter; assert `Disposed==false` — registry owns it)
- `AddTail_AfterDispose_ThrowsObjectDisposed`
- `RemoveTail_DeletesProvidersTailUploadsAndSessionRows_AndEvictsAdapter`
- `RemoveTail_UnknownId_ReturnsInvalidArgument`
- `RemoveTail_DoesNotDeleteRemoteData`
- `ListTails_ReturnsRowPerActiveProvider_WithCounts`
- `ListTails_EmptyWhenNoTails`

Reuses `FaultInjectingStorageProvider` (§3.1), `FakeClock`/`TestNetworkAvailabilityMonitor`, and the existing volume harness.

---

## Acceptance criteria
- [ ] Builds with zero warnings on all targets.
- [ ] All Phase 0–4.5 tests pass after the `RegisterTailAsync`→`AddTailAsync` migration.
- [ ] `AddTailAsync`/`RemoveTailAsync`/`ListTailsAsync` exist with §11 signatures; `RegisterTailAsync` deleted.
- [ ] `AddTailAsync` backfills `TailUploads` for existing files (§11.1).
- [ ] Failure path disposes the constructed adapter exactly once; success path does not (no double-dispose).
- [ ] No credential object in any log template (Principle 26).
- [ ] No new `ErrorCode` values; no UI-framework refs; `dotnet format --verify-no-changes` clean.

## Line-of-code budget
| Area | Lines |
|---|---|
| DTOs (`TailConfiguration`,`TailInfo`) | ~85 |
| `IMutableProviderRegistry` + registry edits | ~60 |
| `FlashSkinkVolume` (3 methods, delete 1, fields) | ~+260 / −145 |
| `VolumeCreationOptions` | ~12 |
| **src delta** | **~420 net** |
| `FakeProviderSetup` + `AddTailAsyncTests` + migrations | ~600 |
| **Total delta** | **~1,020** |

## Non-goals
- No CLI commands, no `skink` binary rename, no embedded guides, no OAuth-capture wiring — all **4.6b**.
- No multi-account/multi-folder per provider type (post-V1).
- No cloud `ProviderConfig` folder-id persistence (re-resolved on open; post-V1).
- No per-tail uploaded-byte counts in `TailInfo` unless Gate 1 asks.
- No edits to `IStorageProvider`/`UploadSession`/`IProviderSetup`/`ProviderCredentials`/`ProviderSetupKind`/`ProviderHealth` (Principle 23).
- No edits to `RangeUploader`/`UploadQueueService`/`BrainMirrorService`.
