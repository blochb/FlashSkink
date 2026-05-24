# PR 4.1 — Provider setup foundation: setup contract, token crypto, brain-backed registry

**Branch:** `pr/4.1-provider-setup-foundation`
**Blueprint sections:** §10.1 (IStorageProvider — context), §10.3 (IProviderSetup, ProviderCredentials, ProviderSetupKind), §10.4 (ProviderHealth — context), §16.2 (Providers table schema), §24.3 (two-stage CLI flow — downstream context), §24.5 (loopback OAuth — context for the IOAuthCaptureFlow seam), §27.1 (FileSystem setup notes).
**Dev plan section:** phase-4-providers §4.1.
**Phase-4 cross-cutting decisions touched:** 1 (frozen contract), 2 (AES-256-GCM/DEK envelope), 3 (single OAuth helper — interface only here), 5 (`ProviderConstants`).

---

## Scope

Lay the foundation that the rest of Phase 4 builds on:

1. Introduce the **provider setup contract** — `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ValidationResult` — in `FlashSkink.Core.Abstractions/Providers/` per Blueprint §10.3. No edits to existing frozen contracts (`IStorageProvider`, `UploadSession`, `ProviderHealth`).
2. Introduce the **OAuth capture seam** — `IOAuthCaptureFlow` and `OAuthCaptureContext` — in `FlashSkink.Core.Abstractions/Providers/`. §4.2 implements `LoopbackOAuthCapture`; §4.3–4.5 consume the interface; §4.6 wires it in the CLI composition root.
3. Introduce the **DEK envelope helper for refresh tokens** — `ProviderTokenCrypto` — in `FlashSkink.Core/Providers/Setup/`. Reuses the same binary envelope shape as `WitnessCrypto` (§3.5.1), implemented independently (cross-cutting decision 2 — "parallel" not "delegating") so the two helpers stay decoupled.
4. Introduce **`ProviderConstants`** in `FlashSkink.Core/Providers/` carrying the cloud-provider root-folder name and the shared subpath prefixes (`_witness/`, `_brain/`, `blobs/`) that §4.3–4.5 will share.
5. Introduce **`FileSystemProviderSetup`** in `FlashSkink.Core/Providers/Setup/` — the first `IProviderSetup` implementation; the only one in this PR because no OAuth is involved. It is the template the cloud setups in §4.3–4.5 follow.
6. Introduce **`BrainBackedProviderRegistry`** in `FlashSkink.Core/Providers/` — the production replacement for `InMemoryProviderRegistry`. Reads `Providers` rows once at volume open, dispatches per `ProviderType` to construct `IStorageProvider` adapters, and caches them for the volume's lifetime. In §4.1 it handles `"filesystem"` only; unknown / cloud rows are logged at `Warning` and skipped (graceful degradation — §4.3–4.5 fill in the dispatch arms).
7. Switch the **default registry** in `FlashSkinkVolume.BuildVolumeFromSessionAsync` (and the matching site in `CreateAsync`'s pre-handshake registry construction) from `InMemoryProviderRegistry` to `BrainBackedProviderRegistry`. Tests that pass an explicit `VolumeCreationOptions.ProviderRegistry` continue to receive their injected registry — the §3.5.2 test pattern is unaffected.

`AddTailAsync` / `RemoveTailAsync` / `ListTailsAsync` are **not** in scope here — those land in §4.6 alongside the deletion of the internal `RegisterTailAsync`. `RegisterTailAsync` continues to require an `InMemoryProviderRegistry`-backed volume (its existing guard); production code paths get `BrainBackedProviderRegistry` and will only register tails via §4.6's public `AddTailAsync`. This deliberately leaves a tail-registration gap in production between this PR and §4.6 — but no production CLI commands trigger tail registration yet, so the gap is invisible to users.

---

## Discrepancies surfaced against the dev-plan text

Three small drifts between phase-4-providers.md §4.1 and the codebase. I'm flagging them so the Gate 1 review can confirm the resolution.

1. **Column name.** The phase plan refers to `Providers.EncryptedRefreshToken`; the actual schema column (V001_InitialSchema.sql line 66 + Blueprint §16.2) is named **`EncryptedToken`**. I will use `EncryptedToken` and the existing `EncryptedClientSecret`.

2. **Vestigial nonce columns.** The schema has separate `TokenNonce` and `ClientSecretNonce` TEXT columns alongside the BLOB ciphertexts. Cross-cutting decision 2 ("same envelope as the witness") commits to a **self-contained binary envelope** (`[0x01][12-byte nonce][ciphertext][16-byte GCM tag]`) — the nonce lives inside the BLOB. Plan: write the full envelope into `EncryptedToken` / `EncryptedClientSecret`; leave `TokenNonce` and `ClientSecretNonce` as `NULL`. The Nonce columns become vestigial; dropping them is a future migration (out of scope here).

3. **`CancellationToken` plumbing.** `BuildVolumeFromSessionAsync` currently does not accept a `CancellationToken`. `BrainBackedProviderRegistry.CreateAsync` performs an async brain read at construction and should honour cancellation (Principle 13 — `ct` always last on public-ish async methods). Plan: extend `BuildVolumeFromSessionAsync`'s signature with `CancellationToken ct` (final parameter, default not used — private static helper), and pass through from the three call sites in `CreateAsync` and `OpenAsync` that already have `ct` in scope.

---

## Files to create

- `src/FlashSkink.Core.Abstractions/Providers/ProviderSetupKind.cs` — `public enum ProviderSetupKind { OAuth, ApiKey, LocalPath }`. ~15 lines.
- `src/FlashSkink.Core.Abstractions/Providers/ProviderCredentials.cs` — `public sealed record ProviderCredentials` with optional `ClientId`, `ClientSecret`. ~20 lines.
- `src/FlashSkink.Core.Abstractions/Providers/ValidationResult.cs` — `public sealed record ValidationResult(bool IsValid, string? Reason)`. ~15 lines.
- `src/FlashSkink.Core.Abstractions/Providers/IProviderSetup.cs` — the §10.3 interface. ~80 lines including XML docs.
- `src/FlashSkink.Core.Abstractions/Providers/IOAuthCaptureFlow.cs` — interface + `OAuthCaptureContext` record. ~70 lines.
- `src/FlashSkink.Core/Providers/ProviderConstants.cs` — `internal static class` with cloud root + subpath constants. ~30 lines.
- `src/FlashSkink.Core/Providers/Setup/ProviderTokenCrypto.cs` — AES-256-GCM envelope helper, structurally parallel to `WitnessCrypto`. ~140 lines.
- `src/FlashSkink.Core/Providers/Setup/FileSystemProviderSetup.cs` — first `IProviderSetup` implementation. ~150 lines.
- `src/FlashSkink.Core/Providers/BrainBackedProviderRegistry.cs` — production `IProviderRegistry`. ~220 lines.
- `tests/FlashSkink.Tests/Providers/Setup/ProviderTokenCryptoTests.cs` — round-trip + tamper tests. ~110 lines.
- `tests/FlashSkink.Tests/Providers/Setup/FileSystemProviderSetupTests.cs` — `ValidatePathAsync` happy + failure paths. ~140 lines.
- `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` — open with no providers, with one filesystem provider, with an unknown type; cache semantics; ListActive returns dictionary keys. ~190 lines.

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — three changes:
  - `BuildVolumeFromSessionAsync`: add `CancellationToken ct` final parameter; switch the `registry ??=` fallback to `BrainBackedProviderRegistry.CreateAsync(brain, dek, loggerFactory, ct)`. Pass `ct` through from the two existing call sites (lines 251 and 388).
  - The mirror registry-construction site inside `CreateAsync` near line 1482 (the handshake-time fallback): same `BrainBackedProviderRegistry.CreateAsync` switch.
  - One XML comment update on `VolumeCreationOptions.ProviderRegistry` is not needed (it already mentions Phase 4); no other doc churn.

## Dependencies

- **NuGet:** none new. `Microsoft.Data.Sqlite`, `Dapper`, and the BCL `System.Security.Cryptography.AesGcm` cover everything this PR needs.
- **Project references:** none new. `Core.Abstractions` continues to have no project references; `Core` already references `Core.Abstractions`.

---

## Public API surface

### `FlashSkink.Core.Abstractions.Providers.ProviderSetupKind` (public enum)

**XML intent:** classifies how a provider obtains the credential material the brain persists.

```
public enum ProviderSetupKind
{
    OAuth = 0,
    ApiKey = 1,
    LocalPath = 2,
}
```

Matches Blueprint §10.3 verbatim. Values are ordered with `OAuth = 0` so the default of an uninitialised field is the most common case.

### `FlashSkink.Core.Abstractions.Providers.ProviderCredentials` (public sealed record)

**XML intent:** carrier for the user-supplied BYOC OAuth app credentials. `null` instance is the convention for `LocalPath` providers.

```
public sealed record ProviderCredentials
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
}
```

No constructor validation — both fields default to `null` so the record can carry partial state during setup. The downstream `IProviderSetup` implementation enforces "ClientId and ClientSecret must both be non-null for OAuth providers" with a `Result.Fail(InvalidArgument)`.

### `FlashSkink.Core.Abstractions.Providers.ValidationResult` (public sealed record)

**XML intent:** outcome of a non-throwing validation call. `IsValid = true` ↔ `Reason == null`.

```
public sealed record ValidationResult(bool IsValid, string? Reason)
{
    public static ValidationResult Valid { get; } = new(true, null);
    public static ValidationResult Invalid(string reason) => new(false, reason);
}
```

The `Valid` singleton avoids allocation in the happy path.

### `FlashSkink.Core.Abstractions.Providers.IProviderSetup` (public interface)

**XML intent:** Blueprint §10.3 contract — drives a brand-new tail through credential collection, validation, token exchange (for OAuth providers), and construction of the runtime `IStorageProvider`. Implementations live alongside their `IStorageProvider` (e.g. `FileSystemProviderSetup` next to `FileSystemProvider`; `GoogleDriveSetup` next to `GoogleDriveProvider` in §4.3).

```
public interface IProviderSetup
{
    string ProviderType { get; }
    string DisplayName { get; }
    ProviderSetupKind SetupKind { get; }

    Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri,
        string codeChallenge,
        ProviderCredentials credentials,
        CancellationToken ct);

    Task<Result<byte[]>> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        ProviderCredentials credentials,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);

    Task<Result<ValidationResult>> ValidatePathAsync(
        string path,
        string skinkRoot,
        CancellationToken ct);

    Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId,
        string displayName,
        byte[] encryptedToken,
        ProviderCredentials credentials,
        string? providerConfigJson,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);
}
```

**Departures from the Blueprint §10.3 literal signature**, with justification:

1. `GetAuthorizationUriAsync` takes a `codeChallenge` (PKCE) — required so the per-provider URL builder can include `code_challenge=…&code_challenge_method=S256` in the authorisation URL. The Blueprint did not anticipate PKCE explicitly; cross-cutting decision 3 (§4.2) commits to PKCE, and this is the natural place for the parameter. RFC 8252 (native app OAuth) requires PKCE.
2. `ExchangeCodeAsync` takes a `codeVerifier` for the corresponding token-exchange request.
3. `ValidatePathAsync` takes a `skinkRoot` so the `FileSystemProviderSetup` can reject paths that are subdirectories of the skink (Blueprint §24.3 — would create a backup loop). Without `skinkRoot` the validation cannot enforce that rule.
4. `CreateProviderAsync` takes `providerId`, `displayName`, and `providerConfigJson` so the returned `IStorageProvider` is fully populated — the runtime instance needs all three to be useful to the upload orchestrator.
5. `dek` parameters typed as `ReadOnlyMemory<byte>` rather than `byte[]` — gives implementations a non-copying handle and lets the caller revoke access by dropping the underlying array. Matches the existing `WitnessStore` / `WitnessCrypto` shape.
6. Every method gains `CancellationToken ct` as its final parameter (Principle 13 — Blueprint §10.3 omitted it; the codebase convention is mandatory).

These departures are **additive on parameter lists** (PKCE, skinkRoot, ct) or **type-strengthening** (ReadOnlyMemory) — they don't break the §10.3 promise. If a Gate 1 reviewer prefers the literal Blueprint signature, escalation path is to update Blueprint §10.3 in this PR.

### `FlashSkink.Core.Abstractions.Providers.IOAuthCaptureFlow` (public interface)

**XML intent:** abstraction over the local-loopback OAuth dance (Blueprint §24.5, cross-cutting decision 3). §4.2 implements; §4.6 wires the CLI composition root; tests inject a fake that returns canned outcomes.

```
public interface IOAuthCaptureFlow
{
    // Allocates a free loopback port, generates PKCE verifier+challenge, returns
    // the context the caller weaves into the provider authorization URL.
    Result<OAuthCaptureContext> Prepare();

    // Launches the system browser to the authorization URL and listens on the
    // prepared loopback redirect until the provider redirects with ?code=...
    // Returns the authorization code; the caller pairs it with context.CodeVerifier
    // when calling IProviderSetup.ExchangeCodeAsync.
    Task<Result<string>> AwaitAuthorizationCodeAsync(
        OAuthCaptureContext context,
        Uri authorizationUri,
        CancellationToken ct);
}

public sealed record OAuthCaptureContext(
    string RedirectUri,
    string CodeChallenge,
    string CodeVerifier);
```

Two-method shape chosen so the caller can build the authorisation URL with the challenge before launching the browser. A single-method "do everything" interface would force a URL-builder callback parameter, which is awkward to test. **Gate 1 question:** is this shape acceptable, or do you want a single-method version?

`Prepare()` is synchronous because port allocation + PKCE generation are non-async; it returns `Result<>` to surface a port-exhaustion failure cleanly.

`OAuthCaptureContext` is a `sealed record` (not a `readonly record struct`) because it carries three reference-type strings — boxing pressure isn't a hot-path concern and `record` reads more naturally with `with`-expressions.

---

## Internal types

### `FlashSkink.Core.Providers.ProviderConstants` (internal static)

```
internal static class ProviderConstants
{
    // Cross-cutting decision 5: the top-level folder all cloud providers
    // store under. FileSystem providers ignore this — their root is configured.
    public const string CloudRootFolderName = "FlashSkink Backup";

    // Relative subpath for witness files, identical across providers.
    // Mirrors WitnessStore.WitnessRemoteName / WitnessRemotePrefix; the
    // existing literals in WitnessStore stay as-is in this PR (rewiring is
    // deferred to §4.3 as part of that PR's natural touch-points).
    public const string WitnessSubpath = "_witness/current.enc";
    public const string WitnessPrefix = "_witness/";

    // Relative subpath for rolling brain mirrors.
    public const string BrainMirrorPrefix = "_brain/";

    // Relative subpath for sharded blobs.
    public const string BlobsPrefix = "blobs/";
}
```

`internal` because no public consumer outside `FlashSkink.Core` needs these.

### `FlashSkink.Core.Providers.Setup.ProviderTokenCrypto` (internal static)

Mirrors `WitnessCrypto` (Identity/) — same envelope format, independently implemented (cross-cutting decision 2). Differs from `WitnessCrypto` in two ways:

- Public API takes/returns `string` (the OAuth refresh token is a string); internally encodes UTF-8.
- Lives in `FlashSkink.Core.Providers.Setup` rather than `Identity` — domain separation; no cross-namespace dependency.

```
internal static class ProviderTokenCrypto
{
    internal const byte CurrentVersion = 0x01;     // mirrors WitnessCrypto
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    private const int Overhead = 1 + NonceSize + TagSize;

    internal static byte[] Encrypt(string plaintext, ReadOnlyMemory<byte> dek);
    internal static bool TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlyMemory<byte> dek, out string plaintext);
}
```

`Encrypt` UTF-8-encodes the plaintext into a `byte[]` (one allocation), encrypts via `AesGcm` into a fresh envelope. `TryDecrypt` returns `false` for any failure (truncated envelope, wrong version, wrong DEK, tampered ciphertext, malformed UTF-8) — never throws. `plaintext` is empty string on failure.

### `FlashSkink.Core.Providers.Setup.FileSystemProviderSetup` (internal sealed)

```
internal sealed class FileSystemProviderSetup : IProviderSetup
{
    private readonly ILogger<FileSystemProviderSetup> _logger;

    public string ProviderType => "filesystem";
    public string DisplayName => "Local folder";
    public ProviderSetupKind SetupKind => ProviderSetupKind.LocalPath;

    public FileSystemProviderSetup(ILogger<FileSystemProviderSetup> logger);

    // OAuth methods return Fail(InvalidArgument) — LocalPath setup does not
    // use OAuth.
    public Task<Result<Uri>> GetAuthorizationUriAsync(...);
    public Task<Result<byte[]>> ExchangeCodeAsync(...);

    // The real method.
    public Task<Result<ValidationResult>> ValidatePathAsync(string path, string skinkRoot, CancellationToken ct);

    // For LocalPath providers, encryptedToken is empty and credentials is null.
    // The rootPath comes from providerConfigJson (deserialised as FileSystemProviderConfig).
    // Constructs a FileSystemProvider via FileSystemProvider.Create.
    public Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId, string displayName,
        byte[] encryptedToken, ProviderCredentials credentials,
        string? providerConfigJson,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);
}
```

Validation rules in `ValidatePathAsync`:

1. `path` is non-empty and well-formed (catch `ArgumentException` from `Path.GetFullPath`).
2. The path exists and is a directory (`Directory.Exists`).
3. The path is writable: attempt a 1-byte probe write under `<path>/_health/`. Failures return `ValidationResult.Invalid("…")` not `Result.Fail` — the validation succeeded as an operation; the answer is "not valid".
4. The fully-resolved path is **not** a subdirectory of the fully-resolved `skinkRoot`. Uses `Path.GetFullPath` on both, then `targetFull.StartsWith(skinkRootFull, StringComparison.OrdinalIgnoreCase)` (with a trailing separator appended to `skinkRootFull` to prevent prefix matches like `/foo` matching `/foobar`).
5. The path is **not** the skink root itself.

Failure of (1)–(3) returns `Result<ValidationResult>.Ok(ValidationResult.Invalid("..."))`. Genuine I/O exceptions during the probe write are caught and returned as `Result<ValidationResult>.Fail(StagingFailed, ..., ex)` — that's a real error, not a validation outcome.

### `FlashSkink.Core.Providers.BrainBackedProviderRegistry` (public sealed)

Public because `VolumeCreationOptions.ProviderRegistry` (public) is typed as `IProviderRegistry` and tests outside the `Core` assembly need to be able to construct one if they want to (none do today; `InMemoryProviderRegistry` is the test default).

```
public sealed class BrainBackedProviderRegistry : IProviderRegistry, IAsyncDisposable
{
    // Cached adapters keyed by ProviderID. Populated by CreateAsync, mutated
    // by Phase-4.6's AddTailAsync / RemoveTailAsync. ConcurrentDictionary
    // because GetAsync runs from the upload orchestrator's background loop.
    private readonly ConcurrentDictionary<string, IStorageProvider> _adapters = new();
    private readonly ILogger<BrainBackedProviderRegistry> _logger;

    // Internal — tests construct via CreateAsync, production via CreateAsync
    // from BuildVolumeFromSessionAsync. The DEK is held for §4.3-4.5 cloud
    // provider construction (refresh-token decryption); §4.1 doesn't use it
    // since FileSystem rows have no encrypted token.
    internal BrainBackedProviderRegistry(ILogger<BrainBackedProviderRegistry> logger);

    public static Task<Result<BrainBackedProviderRegistry>> CreateAsync(
        IBrainAccess brain,
        ReadOnlyMemory<byte> dek,
        ILoggerFactory loggerFactory,
        CancellationToken ct);

    public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct);
    public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct);

    public ValueTask DisposeAsync();   // disposes every cached IStorageProvider that is IAsyncDisposable / IDisposable
}
```

`CreateAsync`:

1. `ct.ThrowIfCancellationRequested()`.
2. Acquire a `BrainScope` (Principle 36).
3. Query: `SELECT ProviderID, ProviderType, DisplayName, ProviderConfig FROM Providers WHERE IsActive = 1 ORDER BY AddedUtc`.
4. For each row, dispatch on `ProviderType`:
   - `"filesystem"`: parse `ProviderConfig` JSON (already-defined `FileSystemProviderConfig`); call `FileSystemProvider.Create(providerId, displayName, rootPath, logger)`. If `Create` returns `Fail`, log at `Warning` and skip — do not abort the open.
   - `"google-drive"` / `"dropbox"` / `"onedrive"`: log at `Warning` ("Provider type '{type}' is not yet supported in this build; the provider will not be available until upgrade. (Phase 4.X)") and skip.
   - any other type: log at `Warning` ("Unknown provider type '{type}'; row will be ignored.") and skip.
5. Each successfully-constructed adapter goes into `_adapters[ProviderID]`.
6. Return `Result<BrainBackedProviderRegistry>.Ok(registry)`. The only error returned by `CreateAsync` itself is `Fail(Unknown)` on an unexpected exception or `Fail(Cancelled)`.

`GetAsync` / `ListActiveProviderIdsAsync` mirror `InMemoryProviderRegistry`'s semantics — dictionary lookup / snapshot of keys.

`DisposeAsync` walks the dictionary and disposes every adapter that implements `IAsyncDisposable` (preferred) or `IDisposable`. Disposal failures are logged at `Warning` and otherwise swallowed (cross-cutting decision 4 — the registry owns the SDK-client lifetime).

---

## Method-body contracts

### `ProviderTokenCrypto.Encrypt(string plaintext, ReadOnlyMemory<byte> dek)`

- Preconditions: `dek.Length == 32` (BCL `AesGcm` enforces; programming error otherwise). `plaintext` may be empty.
- Postconditions: returns a `byte[]` of length `1 + 12 + utf8Length + 16`. The version byte is `0x01`. The nonce is freshly random.
- Throws: never. Programming errors in `AesGcm` (wrong-sized key) bubble up as `CryptographicException` — this is `internal` and the caller is trusted.
- `stackalloc` not used: the envelope is heap-allocated as the return value (callers persist it to SQLite).
- AAD is empty (matches `WitnessCrypto` — the binding to the volume is implicit in the brain row's relationship to the DEK).

### `ProviderTokenCrypto.TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlyMemory<byte> dek, out string plaintext)`

- Returns `false` for: `envelope.Length < Overhead`, `envelope[0] != 0x01`, `AesGcm.Decrypt` throwing `CryptographicException`, or `Encoding.UTF8.GetString` throwing `DecoderFallbackException` (the latter is impossible for ciphertext produced by `Encrypt` but defends against tampered envelopes).
- Sets `plaintext = string.Empty` on failure; never throws.
- Catch ordering inside the helper: `CryptographicException` first (cipher tampering / wrong key); `DecoderFallbackException` second (malformed UTF-8 after decrypt). No bare `catch (Exception)` (Principle 15) — anything else is a programming error and should bubble.

### `FileSystemProviderSetup.ValidatePathAsync(string path, string skinkRoot, CancellationToken ct)`

- `ct.ThrowIfCancellationRequested()` at entry.
- Returns `Result<ValidationResult>.Ok(ValidationResult.Invalid(...))` for: empty path, malformed path (`Path.GetFullPath` throws `ArgumentException`), non-existent directory, path equals skinkRoot, path is a subdirectory of skinkRoot.
- Returns `Result<ValidationResult>.Ok(ValidationResult.Valid)` on success.
- Returns `Result<ValidationResult>.Fail(StagingFailed, ..., ex)` for genuine I/O failures (the probe-write step). Catch ordering: `OperationCanceledException` → `Cancelled`; `IOException` / `UnauthorizedAccessException` → `StagingFailed`; `Exception` → `Unknown`.
- The probe write goes to `<path>/_health/{Guid:N}.probe`, then `File.Delete` it. The `_health` directory is `CreateDirectory`d if absent. This mirrors `FileSystemProvider.Create`'s validation. **Deliberate parallel** with the existing FileSystemProvider validation; the two are run at different stages (setup vs. registry construction).

### `FileSystemProviderSetup.CreateProviderAsync(...)`

- `ct.ThrowIfCancellationRequested()` at entry.
- Deserialises `providerConfigJson` to `FileSystemProviderConfig` (existing internal type). If `providerConfigJson` is null or empty, returns `Fail(InvalidArgument, "FileSystem provider requires providerConfigJson with rootPath.")`.
- Calls `FileSystemProvider.Create(providerId, displayName, config.RootPath, loggerFactory.CreateLogger<FileSystemProvider>())`. Propagates its `Result` directly.
- `encryptedToken`, `credentials`, `dek` are ignored. (Constructor includes them for interface conformance; the XML doc-comment notes the unused parameters.)

### `FileSystemProviderSetup.GetAuthorizationUriAsync(...)` / `ExchangeCodeAsync(...)`

- Both return `Result<*>.Fail(InvalidArgument, "FileSystem provider does not use OAuth.")` synchronously (via `Task.FromResult`).
- The XML doc-comment links to `ProviderSetupKind.LocalPath`.

### `BrainBackedProviderRegistry.CreateAsync(brain, dek, loggerFactory, ct)`

- `ct.ThrowIfCancellationRequested()` at entry.
- Acquires a `BrainScope`. The scope is held only for the duration of the SELECT — adapter construction happens outside the scope (per-row, can be slow for cloud providers; for FileSystem it's fast but still good practice to free the scope as early as possible).
- The internal storage of the constructed registry is `ConcurrentDictionary<string, IStorageProvider>`.
- The DEK is NOT stored on the registry instance — it would be a long-lived secret reference (Principle 31). For §4.1, FileSystem rows don't need the DEK during construction. Cloud sections (§4.3–4.5) will decrypt the refresh token at construction time and pass it into the SDK client constructor; the cleartext refresh token then lives inside the SDK (which is the unavoidable design), but the registry instance itself does not retain it.
- Catch ordering: `OperationCanceledException` → `Cancelled`; `SqliteException` filtered by `Corrupt`/`NotADatabase` → `VolumeCorrupt`; `Exception` → `Unknown`.
- Disposal: `DisposeAsync` walks `_adapters.Values`. Each value is type-tested for `IAsyncDisposable` first, then `IDisposable`. Failures are logged at `Warning`. Disposal is best-effort.

### `BrainBackedProviderRegistry.GetAsync(string providerId, CancellationToken ct)`

- Synchronous dictionary lookup wrapped in `ValueTask.FromResult`.
- Returns `Fail(ProviderUnreachable, $"Provider '{providerId}' is not registered.")` if absent — matches `InMemoryProviderRegistry`'s message verbatim so call-site error handling is identical.
- Does NOT re-read the brain (cross-cutting decision 4 — clients cached for volume lifetime).

### `FlashSkinkVolume.BuildVolumeFromSessionAsync` (modification)

- Signature gains `CancellationToken ct` as the final parameter.
- The two call sites (lines 251, 388) get `, ct` appended.
- The registry-construction line changes from:
  ```csharp
  var registry = options.ProviderRegistry
      ?? new InMemoryProviderRegistry(loggerFactory.CreateLogger<InMemoryProviderRegistry>());
  ```
  to:
  ```csharp
  IProviderRegistry registry;
  if (options.ProviderRegistry is not null)
  {
      registry = options.ProviderRegistry;
  }
  else
  {
      var registryResult = await BrainBackedProviderRegistry.CreateAsync(
          brain, dek, loggerFactory, ct).ConfigureAwait(false);
      if (!registryResult.Success)
      {
          // Failure here means the brain SELECT failed catastrophically. Tear
          // down session+lock the same way the existing failure paths do.
          context.Dispose();
          await session.DisposeAsync().ConfigureAwait(false);
          await instanceLock.DisposeAsync().ConfigureAwait(false);
          throw new InvalidOperationException(
              $"Provider registry failed to initialise: {registryResult.Error!.Code}");
      }
      registry = registryResult.Value!;
  }
  ```
  (The `throw` matches the existing pattern at lines 1356–1364 for `uploadQueueService.Start` failure — `BuildVolumeFromSessionAsync` is a private helper that propagates fatal errors as exceptions; the public `CreateAsync` / `OpenAsync` catch and convert to `Result.Fail`.)
- The matching site inside `CreateAsync` near line 1482 (the pre-handshake registry construction) gets the same switch. That site already has `ct` in scope.

---

## Integration points

- `FileSystemProvider.Create(providerId, displayName, rootPath, logger)` — called by both `FileSystemProviderSetup.CreateProviderAsync` and `BrainBackedProviderRegistry.CreateAsync`. Signature is unchanged.
- `IBrainAccess.LockAsync(ct)` / `BrainScope.Connection` — `BrainBackedProviderRegistry.CreateAsync` performs one `SELECT` against the brain through this contract (Principle 36).
- `FileSystemProviderConfig` (existing `internal sealed record` in `Core/Providers/`) — deserialised by both `FileSystemProviderSetup.CreateProviderAsync` and `BrainBackedProviderRegistry.CreateAsync` to extract `rootPath`. Uses the existing source-generated context.
- `Dapper.SqlMapper` — used for the Providers SELECT in `BrainBackedProviderRegistry`.
- `WitnessCrypto` (existing `internal static` in `Core/Identity/`) — NOT called from this PR; `ProviderTokenCrypto` independently implements the same envelope format. Constants are duplicated by design (cross-cutting decision 2).

---

## Principles touched

- **Principle 1** (Core never throws across public API) — every new public/internal method returns `Result`/`Result<T>` except `ProviderTokenCrypto.TryDecrypt` (try-pattern returning `bool`) and `ValidationResult.Invalid` (pure construction, never throws).
- **Principle 6** (zero-knowledge at every external boundary) — `ProviderTokenCrypto` ensures refresh tokens are AES-256-GCM encrypted with the DEK before leaving the setup-flow and before persisting to the brain. The plaintext refresh token exists only in volatile memory during `IProviderSetup.ExchangeCodeAsync` and inside cloud SDK clients.
- **Principle 12** (OS-agnostic) — `FileSystemProviderSetup.ValidatePathAsync` uses `Path.GetFullPath` and `StringComparison.OrdinalIgnoreCase` for the prefix check; works identically on Windows/Linux/macOS. No platform-branching.
- **Principle 13** (`CancellationToken ct` last) — every new async method has `ct` as its final parameter. `BuildVolumeFromSessionAsync` gains `ct` to match (it was missing before this PR; that omission is corrected here).
- **Principle 14** (`OperationCanceledException` first catch) — all async methods catch `OCE` first and map to `ErrorCode.Cancelled`.
- **Principle 15** (no bare `catch (Exception)` as sole catch) — every catch block in new code has at least one specific type before the fallback.
- **Principle 17** (literal `CancellationToken.None` in compensation) — `BrainBackedProviderRegistry.DisposeAsync` passes `CancellationToken.None` to each adapter's disposal (disposal is non-cancellable).
- **Principle 22** (Dapper acceptable outside hot paths) — the one `SELECT` in `BrainBackedProviderRegistry.CreateAsync` uses Dapper; this runs once per volume open, not in the upload hot loop.
- **Principle 23** (provider contract is frozen — additive only) — no edits to `IStorageProvider`, `UploadSession`, `ProviderHealth`. `IProviderSetup` is *introduced* by this PR (it didn't exist), so it's an additive new contract, not an edit. Departures from the Blueprint §10.3 literal signature are listed under "Public API surface" above for Gate 1 review.
- **Principle 26** (no secrets in logs) — `ProviderTokenCrypto` never logs DEK, plaintext, or ciphertext. `BrainBackedProviderRegistry` logs `ProviderID`, `ProviderType`, `DisplayName` — none are secret. `FileSystemProviderSetup` logs `path` only (a configured directory, not a secret).
- **Principle 28** (Core depends only on MEL abstractions) — every new logger is `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions`.
- **Principle 31** (keys zeroed on volume close) — `BrainBackedProviderRegistry` does NOT store the DEK as a long-lived field; the DEK passed to `CreateAsync` is used immediately and then dropped. Cloud-section future work will need to be careful: the SDK clients hold cleartext refresh tokens, but that's unavoidable for the SDK architecture, and the SDK clients are disposed in `BrainBackedProviderRegistry.DisposeAsync` when the volume closes.

---

## Test spec

### `tests/FlashSkink.Tests/Providers/Setup/ProviderTokenCryptoTests.cs` — class `ProviderTokenCryptoTests`

- `Encrypt_ThenTryDecrypt_RoundTripsPlaintext` — encrypt `"ya29.refresh-token-blob-xxx"`, decrypt with same DEK, assert equal.
- `Encrypt_EmptyPlaintext_RoundTrips` — encrypt `""`, decrypt, assert empty string out.
- `TryDecrypt_WrongDek_ReturnsFalse` — encrypt with DEK A, decrypt with DEK B (32 random bytes); returns `false`, `plaintext == ""`.
- `TryDecrypt_TamperedCiphertext_ReturnsFalse` — flip one byte in the envelope's ciphertext region; returns `false`.
- `TryDecrypt_TamperedTag_ReturnsFalse` — flip one byte in the envelope's tag region; returns `false`.
- `TryDecrypt_TruncatedEnvelope_ReturnsFalse` — pass an envelope of length < `Overhead`; returns `false`.
- `TryDecrypt_WrongVersionByte_ReturnsFalse` — envelope[0] = 0x02; returns `false`.
- `Encrypt_TwoCallsSamePlaintext_ProduceDifferentCiphertexts` — call `Encrypt` twice with identical plaintext + DEK; envelope bytes differ (random nonce).
- `Encrypt_Output_HasExpectedLength` — assert `result.Length == 1 + 12 + utf8Length + 16` for known plaintext.

### `tests/FlashSkink.Tests/Providers/Setup/FileSystemProviderSetupTests.cs` — class `FileSystemProviderSetupTests`

Uses per-test temp directories created via the existing `TempDirectoryFixture` pattern (or constructed inline).

- `ValidatePathAsync_ValidWritableDirectory_ReturnsValid`
- `ValidatePathAsync_NonExistentPath_ReturnsInvalid` — `Reason` contains "does not exist".
- `ValidatePathAsync_EmptyPath_ReturnsInvalid`
- `ValidatePathAsync_PathIsSkinkRoot_ReturnsInvalid` — `Reason` mentions "backup loop" or "skink".
- `ValidatePathAsync_PathIsSubdirectoryOfSkink_ReturnsInvalid` — same.
- `ValidatePathAsync_PathParallelToSkink_ReturnsValid` — `/tmp/foo/skink` and `/tmp/foo/tail` both exist; tail validates as valid.
- `ValidatePathAsync_PrefixMatchButNotSubdirectory_ReturnsValid` — e.g., skink at `/tmp/skink`, tail at `/tmp/skink-backup` (prefix match without separator) — validates as VALID (the StartsWith check uses a trailing separator).
- `ValidatePathAsync_CancellationRequested_ReturnsCancelled`
- `GetAuthorizationUriAsync_ReturnsInvalidArgument`
- `ExchangeCodeAsync_ReturnsInvalidArgument`
- `CreateProviderAsync_NullProviderConfig_ReturnsInvalidArgument`
- `CreateProviderAsync_ValidConfig_ReturnsFileSystemProvider` — provider's `ProviderID` / `DisplayName` / `ProviderType` match the inputs.

### `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` — class `BrainBackedProviderRegistryTests`

Uses a real `FlashSkinkVolume.CreateAsync` to obtain an `IBrainAccess` + DEK, then inserts test rows directly via `connection.ExecuteAsync` before calling `BrainBackedProviderRegistry.CreateAsync` — the existing `VolumeTestHarness` pattern from Orchestration tests.

- `CreateAsync_EmptyProviders_ReturnsEmptyRegistry` — fresh volume, no inserts; `ListActiveProviderIdsAsync` returns empty.
- `CreateAsync_OneFileSystemRow_RegistryHasOneProvider` — insert one Providers row with `ProviderType = "filesystem"` and valid `ProviderConfig`; assert `ListActive` returns one ID, `GetAsync` returns the adapter.
- `CreateAsync_TwoFileSystemRows_RegistryHasBoth`
- `CreateAsync_OneRowWithInvalidRootPath_LogsWarningAndSkips` — `ProviderConfig` rootPath does not exist; assert the row is skipped, `ListActive` empty, log captured at `Warning`.
- `CreateAsync_OneRowWithUnknownProviderType_LogsWarningAndSkips`
- `CreateAsync_OneRowWithCloudProviderType_LogsWarningAndSkips` — `ProviderType = "google-drive"`; skipped (until §4.3).
- `CreateAsync_OneRowWithInactive_NotIncluded` — `IsActive = 0`; the SELECT filters it out.
- `GetAsync_UnknownId_ReturnsProviderUnreachable`
- `GetAsync_KnownId_ReturnsAdapter`
- `DisposeAsync_DisposesAllAdapters` — uses a `FakeStorageProvider` test double that records disposal; assert disposed.
- `Volume_OpenAsync_UsesBrainBackedRegistry_ByDefault` — integration test: create a volume with `VolumeCreationOptions.ProviderRegistry = null`, dispose; reopen with `ProviderRegistry = null`; the upload queue uses a `BrainBackedProviderRegistry`. Asserts via a logger capture or by inserting a row pre-open and observing it in `ListActive`.

---

## Acceptance criteria

- [ ] Builds with zero warnings on all targets.
- [ ] All new tests pass.
- [ ] All existing tests continue to pass (the §3.5.2 test pattern of passing an explicit `InMemoryProviderRegistry` via `VolumeCreationOptions` continues to work).
- [ ] `IProviderSetup` interface and the `ProviderSetupKind` / `ProviderCredentials` / `ValidationResult` companion types exist in `Core.Abstractions/Providers/`.
- [ ] `IOAuthCaptureFlow` + `OAuthCaptureContext` exist in `Core.Abstractions/Providers/`.
- [ ] `ProviderTokenCrypto`, `FileSystemProviderSetup`, `BrainBackedProviderRegistry`, `ProviderConstants` exist in their planned locations.
- [ ] `FlashSkinkVolume.BuildVolumeFromSessionAsync` switches to `BrainBackedProviderRegistry` when `options.ProviderRegistry` is null.
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] No new `ErrorCode` values (Phase 3 / Phase 4 cross-cutting decision continues to hold).
- [ ] No reference to UI frameworks anywhere in the new code (Principles 8–11).

---

## Line-of-code budget

| File | Lines |
|---|---|
| `Core.Abstractions/Providers/ProviderSetupKind.cs` | ~15 |
| `Core.Abstractions/Providers/ProviderCredentials.cs` | ~20 |
| `Core.Abstractions/Providers/ValidationResult.cs` | ~25 |
| `Core.Abstractions/Providers/IProviderSetup.cs` | ~80 |
| `Core.Abstractions/Providers/IOAuthCaptureFlow.cs` | ~70 |
| `Core/Providers/ProviderConstants.cs` | ~30 |
| `Core/Providers/Setup/ProviderTokenCrypto.cs` | ~140 |
| `Core/Providers/Setup/FileSystemProviderSetup.cs` | ~150 |
| `Core/Providers/BrainBackedProviderRegistry.cs` | ~220 |
| `Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~30 |
| **src delta** | **~780** |
| `tests/.../Providers/Setup/ProviderTokenCryptoTests.cs` | ~110 |
| `tests/.../Providers/Setup/FileSystemProviderSetupTests.cs` | ~140 |
| `tests/.../Providers/BrainBackedProviderRegistryTests.cs` | ~190 |
| **test delta** | **~440** |
| **Total delta** | **~1,220** |

---

## Non-goals for §4.1

- Do NOT implement `LoopbackOAuthCapture` — that is §4.2.
- Do NOT implement any cloud provider adapter (`GoogleDriveProvider`, `DropboxProvider`, `OneDriveProvider`) — those are §4.3, §4.4, §4.5.
- Do NOT implement public `AddTailAsync` / `RemoveTailAsync` / `ListTailsAsync` — those land in §4.6 alongside the deletion of internal `RegisterTailAsync`.
- Do NOT delete or modify `InMemoryProviderRegistry` — it remains the test double indefinitely. `RegisterTailAsync` continues to require it.
- Do NOT rewire existing `_witness/` / `_brain/` / `_health/` string literals in `WitnessStore` / `BrainMirrorService` / `FileSystemProvider` to the new `ProviderConstants` symbols. That rewiring lands in §4.3 (Google Drive) as natural touch-points; for §4.1 the constants are introduced for cloud-provider consumption only.
- Do NOT drop the vestigial `TokenNonce` / `ClientSecretNonce` columns from the Providers schema. That is a future schema migration; in §4.1 we write NULL into both.
- Do NOT add any new `ErrorCode` values. `InvalidArgument`, `Cancelled`, `Unknown`, `StagingFailed`, `ProviderUnreachable`, `VolumeCorrupt` cover every failure mode in this PR.
- Do NOT modify Blueprint §10.3 — even though `IProviderSetup`'s actual signatures in this PR include additive parameters (PKCE, skinkRoot, ct). The Gate 1 question above resolves whether Blueprint §10.3 needs an update; if so, that's a follow-up.

---

## Open questions for Gate 1 review

1. **`IOAuthCaptureFlow` shape** — two-method (Prepare + AwaitAuthorizationCodeAsync) or one-method? Two-method is cleaner for PKCE handling and testing; one-method is more compact. Plan commits to two-method.
2. **`IProviderSetup` parameter departures from Blueprint §10.3** — the plan adds PKCE (`codeChallenge`, `codeVerifier`), `skinkRoot`, `providerId`/`displayName`/`providerConfigJson`, and `CancellationToken` parameters. Accept these as additive, or update Blueprint §10.3 in this PR to match?
3. **Vestigial `TokenNonce` / `ClientSecretNonce` columns** — write NULL (current plan) or open a follow-up schema migration to drop them?
4. **Provider type strings** — plan uses `"filesystem"` / `"google-drive"` / `"dropbox"` / `"onedrive"` per Blueprint §10.3 / §16.2 examples. The existing `FileSystemProvider.ProviderType` is `"filesystem"`. Confirmed.
5. **Rewiring `_witness/` / `_brain/` literals to `ProviderConstants`** — explicitly deferred to §4.3 per non-goal #5. Accept the temporary two-sources-of-truth state, or do the rewire here?
