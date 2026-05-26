# PR 4.4 — Dropbox provider and setup

**Branch:** `pr/4.4-dropbox-provider-and-setup`
**Blueprint sections:** §10.1–10.4 (provider contract), §13.6 (blob format — context for what gets uploaded), §15.2 (Dropbox upload-sessions protocol mapping), §15.3 (session lifecycle), §15.4 (48 h session-TTL handling — Dropbox is the canonical case), §15.7 (Dropbox verification — content-hash), §24.3 (Dropbox setup walkthrough), §24.5 (loopback OAuth — consumed via `IOAuthCaptureFlow`), §27.3 (Dropbox provider notes).
**Dev plan section:** phase-4-providers §4.4.
**Phase-4 cross-cutting decisions touched:** 1 (frozen contract — consume, do not edit), 2 (token crypto via `ProviderTokenCrypto`), 3 (OAuth via `IOAuthCaptureFlow`), 4 (HTTP client per-volume — `DropboxClient` constructed in setup, cached by registry), 5 (`ProviderConstants.CloudRootFolderName` is `"FlashSkink Backup"`), 6 (provider-specific verification — see Discrepancy 1 below).

---

## Scope

Ship Dropbox as the second cloud `IStorageProvider`, following the §4.3 Drive shape. Seven production files plus tests:

1. **`DropboxProvider`** (internal sealed, in `Core/Providers/Dropbox/`) — the `IStorageProvider` adapter. Drives Dropbox's `upload_session/start` → `upload_session/append_v2` → `upload_session/finish` lifecycle directly through the SDK's `FilesUserRoutes`. Disposable; owns the `DropboxClientBundle`.
2. **`DropboxSetup`** (internal sealed, in `Core/Providers/Dropbox/`) — the `IProviderSetup` adapter. Wraps the SDK's static `DropboxOAuth2Helper` for authorisation-URL building and code exchange; decrypts the persisted refresh token at registry-construction time and constructs `DropboxProvider`.
3. **`IDropboxClientFactory`** + **`DropboxClientFactory`** (internal, in `Core/Providers/Dropbox/`) — single seam responsible for turning a `(appKey, appSecret, refreshToken)` triple into a ready-to-use `DropboxClient` wrapped in a `DropboxClientBundle`. Tests substitute a fake that returns a `DropboxClient` backed by a recording `HttpMessageHandler` (via `DropboxClientConfig.HttpClient`).
4. **`DropboxClientBundle`** (internal sealed, in `Core/Providers/Dropbox/`) — `IDisposable` wrapper carrying the `DropboxClient` and any per-bundle `HttpClient`. Disposing it disposes the client and the HTTP client.
5. **`DropboxProviderConfig`** (internal sealed record, in `Core/Providers/Dropbox/`) — JSON-serialisable shape persisted into `Providers.ProviderConfig` carrying the `RootPath` (the `/FlashSkink Backup` root, configurable at setup time per §27.3). Source-generated `JsonSerializerContext` matching the `GoogleDriveProviderConfig` / `FileSystemProviderConfig` pattern.
6. **`DropboxContentHash`** (internal static, in `Core/Providers/Dropbox/`) — implementation of Dropbox's content-hash algorithm (4 MiB block-wise SHA-256, then SHA-256 of the concatenated digests). Used only to compute the expected hash for the post-finalisation observability log line in V1; Phase 5 cross-tail verification will consume the same helper.
7. **`BrainBackedProviderRegistry`** (modify) — fill the `"dropbox"` arm in `TryBuildAdapterAsync` so registry construction at volume open resolves Dropbox rows to live `DropboxProvider` instances. The arm decrypts the per-row refresh token + client secret via `ProviderTokenCrypto` and calls `DropboxSetup.CreateProviderFromConfigAsync` (a new sibling of the §4.3 `GoogleDriveSetup.CreateProviderFromConfigAsync` helper).

Plus the supporting integration: `Dropbox.Api` lands as a package reference on `FlashSkink.Core`; the central-package-management pin in `Directory.Packages.props` is corrected from the non-existent `9.0.0` to the latest published `7.0.0` (see Discrepancy 0).

### Out of scope (deferred to later PRs)

- The actual `setup add` / `setup remove` / `setup list` CLI commands — those are §4.6. §4.4 produces only the `IProviderSetup` implementation and the live-registry wiring; the CLI surface lands in §4.6 together with public `AddTailAsync`.
- Witness writes (`_witness/current.enc`) to Dropbox — §3.5.2's witness contract holds across all providers, but exercising it against Dropbox is a §4.6 end-to-end test concern.
- Brain-mirror writes (`_brain/{timestamp}.bin`) to Dropbox — same as witness, routed through the same `BeginUploadAsync` / `UploadRangeAsync` path.
- Cross-tail post-finalisation verification using Dropbox's content-hash — see Discrepancy 1. Phase 5 (healing / cross-tail verification) is the natural home; §4.4 ships the algorithm helper (`DropboxContentHash`) and logs the expected/observed hash at `Information` for observability, but does not implement `ISupportsRemoteHashCheck`.
- Refactoring the §4.3 `RecordingHttpMessageHandler` to live outside the GoogleDrive test folder — see Discrepancy 2. This PR duplicates the (small, self-contained) class under the Dropbox test folder; the extract-to-shared refactor lands when §4.5 OneDrive triples the duplication count.

---

## Discrepancies surfaced against the dev-plan text

Seven drifts. Each is recorded for the audit trail; the Gate 1 review should confirm the resolution before implementation begins.

### Discrepancy 0 — `Dropbox.Api 9.0.0` is not a published version

**Dev-plan claim** (§4.4 NuGet line): *"`Dropbox.Api` (central-package-managed; verify `net10.0` compatibility)."* `Directory.Packages.props` pins `<PackageVersion Include="Dropbox.Api" Version="9.0.0" />` from a Phase 0 PR that pinned plausible-looking values without restoring.

**Reality:** the highest published version on nuget.org is `7.0.0` (verified via the v3-flatcontainer index). `dotnet restore` of `Dropbox.Api 9.0.0` fails with `NU1102: Unable to find package Dropbox.Api with version (>= 9.0.0)`.

**Resolution:** correct the pin to `7.0.0` as part of this PR (`Directory.Packages.props` is one line). The package targets `netstandard2.0` / `netstandard2.1`, both compatible with `net10.0`. Re-restore is automatic on next build. No code touches this pin elsewhere — §4.4 is the first PR to add `<PackageReference Include="Dropbox.Api" />` to any csproj.

### Discrepancy 1 — Dropbox verification cannot use `ISupportsRemoteHashCheck`

**Dev-plan claim** (§4.4 Verification): *"`ISupportsRemoteHashCheck` uses Dropbox's content-hash (4 MB blocks SHA256ed, concatenated and SHA256ed again — the §15.7 row's "content-hash for additional verification" path)."*

**Reality:** `ISupportsRemoteHashCheck.GetRemoteXxHash64Async` returns `Result<ulong>` (64-bit XXHash64). Dropbox's `FileMetadata.ContentHash` is a 256-bit SHA-256-based digest. Same structural mismatch as §4.3's Discrepancy 1 — the two are not interchangeable, and `RangeUploader.FinaliseAndVerifyAsync` already compares `ulong` to `ulong`.

**Resolution: Option A (same as §4.3).** `DropboxProvider` does NOT implement `ISupportsRemoteHashCheck`. The encrypted blob's AES-GCM authentication tag (Principle 6) is the cryptographic authenticator; tampering surfaces at download-time decryption. The Dropbox content-hash from `FileMetadata.ContentHash` and the size from `FileMetadata.Size` ARE logged at `Information` on finalisation:

```
[INF] Dropbox accepted blob for {ProviderId}; id={FileId}, size={Size}, contentHash={ContentHash} (expected={ExpectedHash}).
```

`{ExpectedHash}` is computed lazily inside the provider from the on-disk blob via the new `DropboxContentHash` helper. This is an observability-only comparison in V1 — a mismatch is logged at `Warning` but does NOT fail the upload (the GCM tag is the integrity decision). Phase 5 cross-tail verification will design the capability interface across all three cloud providers at once and may upgrade this to a hard check.

**Phase-impact:** the acceptance criterion *"Witness writes (§3.5.2) are exercised against the new cloud adapters in unit tests (against the SDK fakes) — the witness contract holds across all four providers"* is preserved (the witness contract is the GCM-authenticated blob, not the hash-check capability). The criterion *"No new `ErrorCode` values added"* is preserved.

### Discrepancy 2 — Test fixture reuse strategy

**Constraint:** §4.4's tests need an `HttpMessageHandler` recorder identical in shape to §4.3's `tests/FlashSkink.Tests/Providers/GoogleDrive/RecordingHttpMessageHandler.cs` (used for SDK call interception).

**Three resolution options considered:**

- **A. Copy** the handler into `tests/FlashSkink.Tests/Providers/Dropbox/RecordingHttpMessageHandler.cs` with namespace `FlashSkink.Tests.Providers.Dropbox`. Duplicates ~150 lines of test code.
- **B. Extract** to `tests/FlashSkink.Tests/Providers/RecordingHttpMessageHandler.cs` with namespace `FlashSkink.Tests.Providers`. Update §4.3 Drive tests' `using` lines and the file's namespace declaration. Touches §4.3 test code.
- **C. Cross-namespace `using`** — Dropbox tests import `FlashSkink.Tests.Providers.GoogleDrive`. Ugly; no real test is "in" the Drive namespace by virtue of using a helper from there.

**Selected: Option A.** Rationale:
- The handler is small (~150 lines), self-contained, and stable. Duplication cost is bounded.
- B touches §4.3 test code, expanding the blast radius of §4.4 for no functional gain.
- The natural extraction trigger is §4.5 OneDrive (three users → DRY), at which point we extract once across all three.
- C is namespace-architecture noise.

The duplicated file lives at `tests/FlashSkink.Tests/Providers/Dropbox/RecordingHttpMessageHandler.cs`. The Drive-specific `CannedResponses.ResumableInit` / `Range308` / `Empty308` helpers are NOT carried over; a sibling `DropboxCannedResponses.cs` in the Dropbox folder hosts the Dropbox-shaped canned response helpers.

### Discrepancy 3 — Dropbox has no `AbortUploadAsync` endpoint

**Dev-plan claim** (§4.4): *"`AbortUploadAsync` → Dropbox does not support explicit abort; the session expires naturally after 48 h. The method is a no-op that returns `Ok` (logged at `Debug`)."*

**Reality:** confirmed. Dropbox's upload-sessions API has no abort endpoint; the only ways to free a session are to call `upload_session/finish`, let it time out, or send `close=true` on a final append. None of these is appropriate for "abort an in-progress upload."

**Resolution:** as the dev plan instructs — `AbortUploadAsync` is a no-op returning `Result.Ok()`. The method observes `ct` at entry per Principle 13, then does nothing. Logged at `Debug` once with the session ID for trace continuity.

### Discrepancy 4 — Dropbox has no `GetUploadedBytesAsync` endpoint

**Constraint:** the `IStorageProvider` contract requires `GetUploadedBytesAsync(session, ct)` to return the number of bytes Dropbox has confirmed receiving. Dropbox's REST API does not expose a "how many bytes do you have for this session?" endpoint — the only way to discover the server-side offset is to attempt an append and react to the `UploadSessionAppendError.IncorrectOffset` error, which carries a `CorrectOffset` field.

**Three resolution options considered:**

- **A. Return `session.BytesUploaded`** from the persisted local state without contacting Dropbox. On the next `UploadRangeAsync` call, if Dropbox reports `IncorrectOffset`, the provider maps to `UploadSessionExpired` and `RangeUploader` restarts from byte 0.
- **B. Probe** by sending a zero-byte `UploadSessionAppendV2Async` at the cached offset. If it succeeds, the offset is correct (no bytes appended). If it fails with `IncorrectOffset`, parse the `CorrectOffset` and return it.
- **C. Modify the contract** to surface a richer "out-of-sync" signal. Forbidden by Principle 23 (frozen contract).

**Selected: Option A.** Rationale:
- B uses one extra network round-trip per resume for what is, in steady state, a no-op confirmation.
- The `UploadSessionExpired` → restart-from-byte-0 path is already exercised by Dropbox's 48 h TTL expiry case (see Discrepancy 5), so the failure mode is not a new code path.
- A is exactly what `FileSystemProvider.GetUploadedBytesAsync` does (returns the locally-tracked offset).
- B's small efficiency win (avoid one full restart on offset drift) doesn't justify the network round-trip when drift is rare (only realistic causes: process crash mid-append + partial server commit, which §15.3 already plans for via `UploadSessionExpired`).

The provider's `GetUploadedBytesAsync` returns `Result<long>.Ok(session.BytesUploaded)` synchronously. The method still takes `ct` per Principle 13 and observes it at entry.

### Discrepancy 5 — Mid-upload `IncorrectOffset` is mapped to `UploadSessionExpired`

**Constraint:** When Dropbox's `UploadSessionAppendV2Async` returns `ApiException<UploadSessionAppendError>` with `IncorrectOffset`, the cleanest path back to a known-good state is a full restart. `RangeUploader` already handles `UploadSessionExpired` by calling `BeginUploadAsync` again (per §15.3 step 4 — "Discard session; goto 5").

**Resolution:** the `DropboxProvider.UploadRangeAsync` catch block for `ApiException<UploadSessionAppendError>` filtered on `AsIncorrectOffset` returns `Result.Fail(ErrorCode.UploadSessionExpired, ...)`. Similarly for `ApiException<UploadSessionLookupError>` with `AsNotFound` (the session-id-itself-is-stale case after 48 h TTL).

This preserves Principle 23 (no contract change) and cross-cutting decision 6 (no `RangeUploader` modifications). The cost is restart-from-byte-0 for the rare drift case; the simplicity payoff is one branch in the catch block.

### Discrepancy 6 — `DropboxOAuth2Helper.ProcessCodeFlowAsync` is a static method (not directly mockable)

**Constraint:** the Dropbox SDK exposes its token-exchange flow as a static method:
```
static Task<OAuth2Response> ProcessCodeFlowAsync(
    string code, string appKey, string appSecret, string redirectUri,
    HttpClient client, string codeVerifier)
```

This is desirable functionally (PKCE handling, correct request shape, JSON parsing, error mapping all live in the SDK) but resists direct test injection.

**Resolution:** `DropboxSetup` consumes the static helper directly. The `HttpClient` parameter is the test injection seam — tests pass an `HttpClient` backed by `RecordingHttpMessageHandler`, and the recorder captures the POST to `https://api.dropboxapi.com/oauth2/token` and returns a canned `OAuth2Response`-shaped JSON. The SDK does the parsing; our test verifies the request shape (form body fields, URL) and the returned encrypted-envelope shape.

No additional abstraction layer is introduced. This mirrors the §4.3 Drive setup pattern (which hand-rolls the POST through `_tokenExchangeClient`) but in §4.4 the SDK does the POST for us.

### Discrepancy 7 — `UploadSession.SessionUri` carries both the Dropbox session ID and the destination path

**Constraint:** Dropbox's `UploadSessionFinishAsync` requires a `CommitInfo(path, mode, ...)` parameter naming the destination path. The path is supplied at `BeginUploadAsync` (as `remoteName`) but the `UploadSession` record returned to the caller has no field for it — `SessionUri` is the only opaque-state field. After process restart, `BrainBackedProviderRegistry` rebuilds adapters and `RangeUploader` rehydrates `UploadSession` from `UploadSessions.SessionUri` — without the path persisted there, the provider cannot finalise.

**Resolution:** encode both fields into the `SessionUri` string as a JSON envelope and decode on every session-using call. Format:

```json
{"sid":"abc123-xyz","path":"/FlashSkink Backup/blob.bin"}
```

A small internal helper pair `DropboxSessionUri.Encode(string sessionId, string remotePath)` / `DropboxSessionUri.TryDecode(string raw, out (string SessionId, string RemotePath))` lives in `DropboxProvider.cs`. The `UploadSessions.SessionUri` SQLite TEXT column stores the encoded form; the encrypted at-rest property (already provided by SQLCipher per Phase 1) covers confidentiality. No new `ErrorCode` values needed.

**Robustness contract for `TryDecode` (per Gate 1 review point #1).** The method NEVER throws. Every failure mode returns `false` and is recoverable by the caller:
- `null` / empty / whitespace input — `false`.
- Not valid JSON — caught `JsonException` returns `false`.
- Valid JSON but missing required fields (`sid` or `path`) — `false`.
- Empty / whitespace `sid` or `path` after parse — `false`.
- Schema drift (e.g. older or newer FlashSkink wrote a different shape) — `false`.
- Any other unexpected exception inside the parse (defensive `catch (Exception)`) — `false` and a `Debug`-level log line for diagnostics (no exception details logged at `Warning`+ because malformed input here is a soft expected failure, not an error).

Every caller site (`UploadRangeAsync`, `FinaliseUploadAsync`) maps a `TryDecode` returning `false` to `Result.Fail(ErrorCode.UploadSessionExpired, "SessionUri envelope is malformed or unrecognised; restarting upload.")`. `RangeUploader` already treats `UploadSessionExpired` as "discard session, call `BeginUploadAsync` again from byte 0" per §15.3 step 4, so the upload self-heals.

This robustness contract is exercised by a dedicated test row (`DropboxSessionUri_TryDecode_Robustness_Theory`) with cases: null, empty, whitespace, garbage non-JSON, valid JSON with missing `sid`, valid JSON with missing `path`, valid JSON with empty `sid`, valid JSON with empty `path`, extra fields (must still decode successfully — forward-compat), nested-object-shape, oversized input (10 KiB).

This is internal to `DropboxProvider` — the public contract (the `UploadSession` record) is unchanged.

---

## Files to create

- `src/FlashSkink.Core/Providers/Dropbox/DropboxProvider.cs` — the `IStorageProvider` + `IAsyncDisposable` adapter. ~520 lines.
- `src/FlashSkink.Core/Providers/Dropbox/DropboxSetup.cs` — the `IProviderSetup` adapter. ~320 lines.
- `src/FlashSkink.Core/Providers/Dropbox/IDropboxClientFactory.cs` — internal interface for the SDK-construction seam. ~30 lines.
- `src/FlashSkink.Core/Providers/Dropbox/DropboxClientFactory.cs` — production implementation. ~60 lines.
- `src/FlashSkink.Core/Providers/Dropbox/DropboxClientBundle.cs` — disposable wrapper. ~40 lines.
- `src/FlashSkink.Core/Providers/Dropbox/DropboxProviderConfig.cs` — JSON-serialisable record + source-generated context. ~50 lines.
- `src/FlashSkink.Core/Providers/Dropbox/DropboxContentHash.cs` — internal static helper computing the Dropbox content-hash over a `Stream`. ~80 lines.
- `tests/FlashSkink.Tests/Providers/Dropbox/DropboxProviderTests.cs` — unit tests for the adapter using a recording handler. ~620 lines.
- `tests/FlashSkink.Tests/Providers/Dropbox/DropboxSetupTests.cs` — unit tests for the setup adapter. ~360 lines.
- `tests/FlashSkink.Tests/Providers/Dropbox/DropboxContentHashTests.cs` — known-vector tests for the algorithm. ~100 lines.
- `tests/FlashSkink.Tests/Providers/Dropbox/FakeDropboxClientFactory.cs` — test seam returning a `DropboxClient` backed by a `RecordingHttpMessageHandler`. ~80 lines.
- `tests/FlashSkink.Tests/Providers/Dropbox/RecordingHttpMessageHandler.cs` — duplicated from `GoogleDrive/` per Discrepancy 2. ~170 lines (handler + records).
- `tests/FlashSkink.Tests/Providers/Dropbox/DropboxCannedResponses.cs` — Dropbox-shaped canned response helpers. ~90 lines.

## Files to modify

- `Directory.Packages.props` — correct `<PackageVersion Include="Dropbox.Api" Version="9.0.0" />` → `Version="7.0.0"`. One-line change (Discrepancy 0).
- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `<PackageReference Include="Dropbox.Api" />`.
- `src/FlashSkink.Core/Providers/BrainBackedProviderRegistry.cs` — fill the `"dropbox"` arm in `TryBuildAdapterAsync` with `TryBuildDropboxAdapterAsync`. Add `IDropboxClientFactory` constructor parameter to the existing internal `CreateAsync(IBrainAccess, ReadOnlyMemory<byte>, IGoogleDriveClientFactory, ILoggerFactory, CancellationToken)` overload — extending the signature additively to `CreateAsync(IBrainAccess, ReadOnlyMemory<byte>, IGoogleDriveClientFactory, IDropboxClientFactory, ILoggerFactory, CancellationToken)`. The public `CreateAsync` signature continues to take only `(brain, dek, loggerFactory, ct)` and internally constructs both production factories. ~110 lines of delta.
- `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` — add Dropbox dispatch tests (mirror the §4.3 Drive tests). ~180 lines of delta.

---

## Dependencies

- **NuGet:** `Dropbox.Api 7.0.0` (correcting the existing `9.0.0` pin per Discrepancy 0). The package targets `netstandard2.0` / `netstandard2.1`; verified `net10.0`-compatible by restoring it locally. Transitive deps: `Newtonsoft.Json` (used by the SDK internally for JSON parsing — pulled transitively).
- **Project references:** none new.
- **BCL:** `System.Net.Http` (already available), `System.Text.Json` (already used), `System.Security.Cryptography` (`SHA256` for the content-hash algorithm).

---

## Public API surface

§4.4 adds **no public types**. All new types are `internal sealed`:

- `DropboxProvider` — implements the existing public `IStorageProvider` contract.
- `DropboxSetup` — implements the existing public `IProviderSetup` contract.
- `DropboxClientFactory` / `IDropboxClientFactory` / `DropboxClientBundle` — internal seam.
- `DropboxProviderConfig` — internal record.
- `DropboxContentHash` — internal static helper.

The §4.1 `BrainBackedProviderRegistry`'s `public static CreateAsync(brain, dek, loggerFactory, ct)` signature is unchanged. A new `internal static CreateAsync` overload that takes both `IGoogleDriveClientFactory` and `IDropboxClientFactory` is added; the existing `internal static CreateAsync(brain, dek, IGoogleDriveClientFactory, loggerFactory, ct)` overload is preserved by delegating to the new one with the production `new DropboxClientFactory()` supplied (so §4.3's drive-only tests continue to compile).

---

## Internal types

### `FlashSkink.Core.Providers.Dropbox.DropboxProviderConfig` (internal sealed record)

```csharp
/// <summary>
/// Persisted JSON shape for <c>Providers.ProviderConfig</c> on Dropbox rows. Carries the
/// root path under which all FlashSkink objects live in the user's Dropbox account.
/// </summary>
internal sealed record DropboxProviderConfig
{
    /// <summary>Root path (e.g. <c>"/FlashSkink Backup"</c>); never null.</summary>
    [JsonPropertyName("rootPath")]
    public required string RootPath { get; init; }
}

[JsonSerializable(typeof(DropboxProviderConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DropboxProviderConfigJsonContext : JsonSerializerContext;
```

Default `RootPath` at first setup time: `"/" + ProviderConstants.CloudRootFolderName` (i.e. `"/FlashSkink Backup"`).

### `FlashSkink.Core.Providers.Dropbox.IDropboxClientFactory` (internal interface)

```csharp
internal interface IDropboxClientFactory
{
    /// <summary>
    /// Constructs a <see cref="DropboxClientBundle"/> from the user's BYOC OAuth app key/secret
    /// plus the freshly-decrypted refresh token. The SDK manages access-token refresh internally
    /// from the refresh token.
    /// </summary>
    /// <param name="appKey">Dropbox app key (BYOC; the SDK's "appKey" terminology for ClientId).</param>
    /// <param name="appSecret">Dropbox app secret (BYOC; cleartext — must not be logged).</param>
    /// <param name="refreshToken">OAuth refresh token (cleartext — must not be logged).</param>
    /// <param name="loggerFactory">Logger factory for the bundle's own diagnostics.</param>
    Result<DropboxClientBundle> Create(
        string appKey,
        string appSecret,
        string refreshToken,
        ILoggerFactory loggerFactory);
}
```

### `FlashSkink.Core.Providers.Dropbox.DropboxClientBundle` (internal sealed)

```csharp
internal sealed class DropboxClientBundle : IDisposable
{
    public required DropboxClient Client { get; init; }

    /// <summary>The HTTP client supplied to <see cref="DropboxClientConfig.HttpClient"/>. Disposed by this bundle.</summary>
    public required HttpClient HttpClient { get; init; }

    public void Dispose()
    {
        try { Client.Dispose(); } catch { /* swallow */ }
        try { HttpClient.Dispose(); } catch { /* swallow */ }
    }
}
```

### `FlashSkink.Core.Providers.Dropbox.DropboxClientFactory` (internal sealed) — full body sketch

```csharp
internal sealed class DropboxClientFactory : IDropboxClientFactory
{
    private const string UserAgent = "FlashSkink/1.0";

    public Result<DropboxClientBundle> Create(
        string appKey, string appSecret, string refreshToken, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrEmpty(appKey))   { return Result<DropboxClientBundle>.Fail(InvalidArgument, "appKey is required."); }
        if (string.IsNullOrEmpty(appSecret)) { return Result<DropboxClientBundle>.Fail(InvalidArgument, "appSecret is required."); }
        if (string.IsNullOrEmpty(refreshToken)) { return Result<DropboxClientBundle>.Fail(InvalidArgument, "refreshToken is required."); }

        HttpClient? http = null;
        DropboxClient? client = null;
        try
        {
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var config = new DropboxClientConfig(UserAgent) { HttpClient = http };
            client = new DropboxClient(refreshToken, appKey, appSecret, config);

            return Result<DropboxClientBundle>.Ok(new DropboxClientBundle
            {
                Client = client,
                HttpClient = http,
            });
        }
        catch (Exception ex)
        {
            try { client?.Dispose(); } catch { /* swallow */ }
            try { http?.Dispose(); } catch { /* swallow */ }
            return Result<DropboxClientBundle>.Fail(Unknown, "Failed to construct Dropbox SDK client.", ex);
        }
    }
}
```

### `FlashSkink.Core.Providers.Dropbox.DropboxProvider` (internal sealed) — surface sketch

```csharp
internal sealed partial class DropboxProvider : IStorageProvider, IAsyncDisposable
{
    // Blueprint §15.2: 48 h from last append.
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(48);

    private readonly DropboxClientBundle _bundle;
    private readonly string _rootPath;
    private readonly ILogger<DropboxProvider> _logger;
    private int _disposed;

    public string ProviderID { get; }
    public string ProviderType => "dropbox";
    public string DisplayName { get; }

    /// <summary>Root path resolved at setup time; exposed for §4.6 persistence.</summary>
    internal string RootPath => _rootPath;

    internal DropboxProvider(
        string providerId, string displayName,
        DropboxClientBundle bundle, string rootPath,
        ILogger<DropboxProvider> logger) { … }

    // Public IStorageProvider surface — see Method-body contracts below.
    public Task<Result<UploadSession>> BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct);
    public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct);
    public Task<Result> UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct);
    public Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct);
    public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct);
    public Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct);
    public Task<Result> DeleteAsync(string remoteId, CancellationToken ct);
    public Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct);
    public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct);
    public Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct);
    public Task<Result<long>> GetUsedBytesAsync(CancellationToken ct);
    public Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct);

    public ValueTask DisposeAsync();
}
```

### `FlashSkink.Core.Providers.Dropbox.DropboxSetup` (internal sealed) — surface sketch

```csharp
internal sealed partial class DropboxSetup : IProviderSetup
{
    // Scopes per Dropbox docs (account_info.read is needed for GetSpaceUsageAsync).
    private static readonly string[] Scopes =
        ["files.content.write", "files.content.read", "account_info.read"];

    private readonly IDropboxClientFactory _clientFactory;
    private readonly HttpClient _oauthHttpClient;
    private readonly bool _ownsOauthClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DropboxSetup> _logger;

    public string ProviderType => "dropbox";
    public string DisplayName => "Dropbox";
    public ProviderSetupKind SetupKind => ProviderSetupKind.OAuth;

    public DropboxSetup(ILoggerFactory loggerFactory)
        : this(new DropboxClientFactory(), new HttpClient(), ownsOauthClient: true, loggerFactory) { }

    internal DropboxSetup(
        IDropboxClientFactory clientFactory,
        HttpClient oauthHttpClient,
        ILoggerFactory loggerFactory)
        : this(clientFactory, oauthHttpClient, ownsOauthClient: false, loggerFactory) { }

    private DropboxSetup(IDropboxClientFactory cf, HttpClient http, bool owns, ILoggerFactory lf) { … }

    public Task<Result<Uri>> GetAuthorizationUriAsync(string redirectUri, string codeChallenge, ProviderCredentials credentials, CancellationToken ct);
    public Task<Result<byte[]>> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, ProviderCredentials credentials, ReadOnlyMemory<byte> dek, CancellationToken ct);
    public Task<Result<ValidationResult>> ValidatePathAsync(string path, string skinkRoot, CancellationToken ct);
    public Task<Result<IStorageProvider>> CreateProviderAsync(string providerId, string displayName, byte[] encryptedToken, ProviderCredentials credentials, string? providerConfigJson, ReadOnlyMemory<byte> dek, CancellationToken ct);

    internal Task<Result<IStorageProvider>> CreateProviderFromConfigAsync(
        string providerId, string displayName,
        string appKey, string appSecret, string refreshToken,
        string? persistedRootPath,
        CancellationToken ct);
}
```

### `FlashSkink.Core.Providers.Dropbox.DropboxContentHash` (internal static) — surface sketch

```csharp
internal static class DropboxContentHash
{
    public const int BlockSize = 4 * 1024 * 1024;
    private const int DigestSize = 32; // SHA-256

    /// <summary>
    /// Computes Dropbox's content-hash over <paramref name="source"/>: SHA-256 of each
    /// 4 MiB block, concatenate the digests, SHA-256 the concatenation, hex-encode lowercase.
    /// Streams the input — never materialises the full file in memory regardless of size.
    /// Returns the SHA-256 of the empty string
    /// (<c>e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855</c>) for zero-byte
    /// input (matches Dropbox's documented behaviour for empty uploads).
    /// Returns <see cref="string.Empty"/> if <paramref name="ct"/> is cancelled before completion.
    /// </summary>
    public static async Task<string> ComputeAsync(Stream source, CancellationToken ct);
}
```

**Memory contract (per Gate 1 review point #2).** The implementation MUST stream — it must process arbitrarily large inputs (V1 supports files up to 100 GiB per Blueprint §13) with bounded memory:

| Allocation | Size | Lifetime |
|---|---|---|
| Read buffer for one block | exactly `BlockSize` (4 MiB) | one — rented from `ArrayPool<byte>.Shared` at entry, returned in `finally` |
| Concatenation accumulator | grows by 32 B per block (one digest), capacity hint = `4` blocks initial | reused across blocks; lives until the outer SHA-256 finalisation |
| Final outer hash | 32 B `stackalloc Span<byte>` | scope of one local statement (synchronous) |

For a 10 GiB file: the buffer is the one rented 4 MiB chunk + roughly `2560 × 32 B ≈ 80 KiB` of accumulated block digests. Total resident memory under 5 MiB regardless of input size. No `MemoryStream`, no `byte[]` copy of the file, no `ReadAllBytesAsync`.

Implementation shape:

```csharp
public static async Task<string> ComputeAsync(Stream source, CancellationToken ct)
{
    if (ct.IsCancellationRequested) { return string.Empty; }

    byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
    try
    {
        // Concatenated per-block digests grow by 32 B each; init capacity = a few blocks.
        using var concat = new MemoryStream(capacity: DigestSize * 8);
        bool anyBlock = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // ReadAtLeastAsync drains until BlockSize bytes are obtained or EOF — Dropbox's
            // algorithm requires fixed BlockSize blocks except for the last.
            int read = await source.ReadAtLeastAsync(
                buffer.AsMemory(0, BlockSize),
                minimumBytes: BlockSize,
                throwOnEndOfStream: false,
                cancellationToken: ct).ConfigureAwait(false);

            if (read == 0)
            {
                // Empty input: Dropbox spec says hash is SHA-256("") — handle below the loop.
                break;
            }

            Span<byte> blockDigest = stackalloc byte[DigestSize];
            SHA256.HashData(buffer.AsSpan(0, read), blockDigest);
            concat.Write(blockDigest);
            anyBlock = true;

            if (read < BlockSize) { break; } // tail block — done.
        }

        // Outer hash over concat (or over empty when no bytes were read).
        Span<byte> finalDigest = stackalloc byte[DigestSize];
        if (anyBlock)
        {
            SHA256.HashData(concat.GetBuffer().AsSpan(0, (int)concat.Length), finalDigest);
        }
        else
        {
            SHA256.HashData(ReadOnlySpan<byte>.Empty, finalDigest);
        }
        return Convert.ToHexStringLower(finalDigest);
    }
    catch (OperationCanceledException)
    {
        return string.Empty;
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
    }
}
```

Allocation-conscious per Principle 18 — though the upload pipeline isn't the V1 consumer (the post-finalisation log line is currently the only call site, and §4.4 ultimately disables even that — see the `FinaliseUploadAsync` method-body contract), Phase 5 cross-tail verification will run this against every uploaded blob during recovery scans. Getting the memory shape right now means Phase 5 has nothing to retrofit.

---

## Method-body contracts

### `DropboxProvider.BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()` at entry. Validate `remoteName` non-empty, `totalBytes >= 0`.
2. Compute the destination path: `var destPath = _rootPath + (remoteName.StartsWith("/") ? remoteName : "/" + remoteName);`. (Dropbox does support subfolders unlike Drive, so paths are not flattened — see Out-of-scope.)
3. Call `_bundle.Client.Files.UploadSessionStartAsync(close: false, sessionType: null, contentHash: null, body: Stream.Null)`. Returns `UploadSessionStartResult` with `SessionId`.
4. Encode `(SessionId, destPath)` into the `SessionUri` field via `DropboxSessionUri.Encode`.
5. Return `Result<UploadSession>.Ok(new UploadSession { SessionUri = <encoded>, ExpiresAt = UtcNow + 48h, BytesUploaded = 0, TotalBytes = totalBytes })`. The caller stamps `FileID`/`ProviderID`.

**Failure paths:**

- `OperationCanceledException` → `Cancelled`.
- `AuthException` (HTTP 401 or token-itself failure) → `TokenRefreshFailed`.
- `ApiException<UploadSessionStartError>` filtered on `AsPayloadTooLarge` → `UploadFailed` with metadata.
- `HttpRequestException` → `ProviderUnreachable`.
- `RateLimitException` (SDK-defined) → `ProviderRateLimited`.
- Generic `Exception` → `Unknown`.

### `DropboxProvider.GetUploadedBytesAsync(UploadSession session, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()` at entry.
2. Per Discrepancy 4: return `Result<long>.Ok(session.BytesUploaded)` without contacting Dropbox.
3. The local copy is authoritative; mid-upload drift surfaces via `UploadSessionExpired` on the next `UploadRangeAsync` per Discrepancy 5.

(`Task.FromResult` synchronous; no exception block needed beyond the entry cancel-check.)

### `DropboxProvider.UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate `offset >= 0`, `data.Length > 0`, `offset + data.Length <= session.TotalBytes`.
2. Decode `session.SessionUri` via `DropboxSessionUri.TryDecode`. On decode failure → `Result.Fail(UploadSessionExpired, "Malformed SessionUri envelope.")`.
3. Build a `UploadSessionCursor(decoded.SessionId, (ulong)offset)`.
4. Wrap `data` in a `MemoryStream` (or `ReadOnlyMemoryContent`-equivalent stream — Dropbox's SDK takes `Stream` for the body).
5. Call `_bundle.Client.Files.UploadSessionAppendV2Async(cursor, close: false, contentHash: null, body: stream)`. The SDK manages access-token refresh internally; we don't need a retry-on-401 loop like Drive.
6. On success: `Result.Ok()`.

**Failure paths — exhaustive mapping (per Gate 1 review point #3).**

The Dropbox SDK surfaces failures as a mix of typed `ApiException<TError>` (for documented domain errors), `AuthException` (for credential failures), `RateLimitException` (for 429 rate-limit responses with a `RetryAfter`), and the standard BCL exceptions for network/transport. The catch ordering goes specific-typed → AuthException → RateLimitException → networking → fallback. **Every catch is mapped to one of the existing `ErrorCode` values from `RangeUploader`'s retry classifier so the calling worker knows whether to retry, restart, or fail:**

| Exception (caught) | Filter | `ErrorCode` | RangeUploader behaviour |
|---|---|---|---|
| `OperationCanceledException` | — | `Cancelled` | propagate up the worker (non-retryable) |
| `ApiException<UploadSessionAppendError>` | `e.ErrorResponse.IsIncorrectOffset` | `UploadSessionExpired` | restart from byte 0 |
| `ApiException<UploadSessionAppendError>` | `IsNotFound` or `IsClosed` | `UploadSessionExpired` | restart from byte 0 |
| `ApiException<UploadSessionAppendError>` | `IsTooLarge` or `IsPayloadTooLarge` | `UploadFailed` | fail (configuration bug) |
| `ApiException<UploadSessionAppendError>` | `IsContentHashMismatch` | `UploadFailed` | fail (the bytes we sent ≠ what Dropbox received — defect) |
| `ApiException<UploadSessionAppendError>` | `IsConcurrentSessionInvalidOffset` / `IsConcurrentSessionInvalidDataSize` | `UploadSessionExpired` | restart |
| `ApiException<UploadSessionAppendError>` | `IsOther` (catch-all) | `UploadFailed` | fail |
| `ApiException<UploadSessionLookupError>` | any (rarely surfaced here, kept for safety) | `UploadSessionExpired` | restart |
| `AuthException` | `e.StatusCode == 401` (typical) | `TokenRefreshFailed` | fail (credentials are gone) |
| `RateLimitException` | (SDK-defined, derived from `StructuredException`) | `ProviderRateLimited` | back off + retry |
| `BadInputException` (SDK, derives from `HttpException`) | — | `UploadFailed` | fail (request shape bug) |
| `RetryException` (SDK, derives from `HttpException`; raised on SDK's own retry signal) | `e.IsRateLimit == true` | `ProviderRateLimited` | back off + retry |
| `RetryException` | `e.IsRateLimit == false` (transient 5xx the SDK flagged) | `ProviderUnreachable` | back off + retry |
| `HttpException` (catch after `BadInputException` and `RetryException` so the more-specific subclasses bind first) | `e.StatusCode >= 500` | `ProviderUnreachable` | back off + retry (transient — Dropbox 5xx) |
| `HttpException` | `e.StatusCode == 408` (request timeout) | `ProviderUnreachable` | back off + retry (transient) |
| `HttpException` | other 4xx (other than 401 / 429 handled above) | `UploadFailed` | fail |
| `HttpRequestException` (BCL — DNS, TCP, TLS, etc.) | — | `ProviderUnreachable` | back off + retry (transient) |
| `TaskCanceledException` not from `ct` (BCL — `HttpClient` timeout) | `e.InnerException is TimeoutException` OR `ct.IsCancellationRequested == false` | `ProviderUnreachable` | back off + retry (transient timeout) |
| `IOException` (BCL — socket closed mid-read, TLS abort) | — | `ProviderUnreachable` | back off + retry (transient) |
| `Exception` (final fallback per Principle 15) | — | `Unknown` | fail |

The same table applies to `BeginUploadAsync`, `FinaliseUploadAsync`, `DownloadAsync`, `DeleteAsync`, `ExistsAsync`, `ListAsync`, `CheckHealthAsync`, `GetUsedBytesAsync`, `GetQuotaBytesAsync` — only the typed `ApiException<TError>` parameter changes (`UploadSessionStartError` for start, `UploadSessionFinishError` for finish, `DownloadError` for download, etc.). The transient-vs-fail split (`ProviderUnreachable` for 5xx / timeouts / network, `UploadFailed` for 4xx / domain failures, `ProviderRateLimited` for 429) is identical across methods.

**Why this matters:** the `RangeUploader` retry classifier in §3.3 already treats `ProviderUnreachable` and `ProviderRateLimited` as retryable with backoff, `UploadSessionExpired` as restart-from-byte-0, and `UploadFailed` / `TokenRefreshFailed` / `Unknown` as fail-and-publish. Mapping every Dropbox-side failure into one of those four buckets is what keeps the calling worker behaving correctly without ever needing Dropbox-specific knowledge.

**`TaskCanceledException` disambiguation.** `HttpClient.SendAsync` raises `TaskCanceledException` (a subclass of `OperationCanceledException`) on either user cancellation or its own internal `Timeout`. The standard pattern is:
- If `ct.IsCancellationRequested` → user cancellation → `Cancelled`.
- Else → `HttpClient` timeout → `ProviderUnreachable` (transient).

This is checked inside the `catch (OperationCanceledException ex)` block before mapping. The `Cancelled` path is the only one that maps to non-retryable cancellation.

**`HttpException` is the SDK's transport wrapper.** Dropbox.Api 7.0.0 derives its own `HttpException` class from `Exception` to carry the HTTP status code when transport-level errors occur outside the typed `ApiException<T>` path (e.g. 503 from a load balancer with no Dropbox body). Our catch chain filters `HttpException` before the generic `Exception` fallback so transient 5xx / 408 get the right transient classification.

**Tests cover every row of the table.** The `DropboxProviderTests` suite has one `[Theory]` per error category proving the mapping holds:
- `UploadRangeAsync_SdkHttp5xx_ReturnsProviderUnreachable_Theory` over `[500, 502, 503, 504]`.
- `UploadRangeAsync_SdkHttp4xx_ReturnsUploadFailed_Theory` over `[400, 404 (not session-not-found), 409, 422]`.
- `UploadRangeAsync_TaskCanceledFromHttpTimeout_ReturnsProviderUnreachable`.
- `UploadRangeAsync_NetworkAbort_ReturnsProviderUnreachable` over `[HttpRequestException, IOException]`.
- `UploadRangeAsync_RateLimit429_ReturnsProviderRateLimited`.
- `UploadRangeAsync_AuthException401_ReturnsTokenRefreshFailed`.
- `UploadRangeAsync_AppendError_Theory` over every nested `UploadSessionAppendError` variant.

### `DropboxProvider.FinaliseUploadAsync(UploadSession session, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()`. Decode `session.SessionUri`.
2. Build `var cursor = new UploadSessionCursor(decoded.SessionId, (ulong)session.TotalBytes);`.
3. Build `var commit = new CommitInfo(decoded.RemotePath, WriteMode.Overwrite.Instance, autorename: false, clientModified: null, mute: true, propertyGroups: null, strictConflict: false);`. `mute=true` suppresses the user-facing notification in the Dropbox client; `WriteMode.Overwrite` because we want subsequent uploads of the same blob (e.g. brain mirror at the same `_brain/{ts}.bin`) to replace rather than auto-rename.
4. Call `var metadata = await _bundle.Client.Files.UploadSessionFinishAsync(cursor, commit, contentHash: null, body: Stream.Null)`. Returns `FileMetadata` with `Id` (e.g. `id:abc123def`), `Size`, `ContentHash`.
5. **Observability:** if the blob is small enough to compute the expected hash cheaply (V1: always — we own the staged blob on the skink and can re-open it), compute `expectedHash = await DropboxContentHash.ComputeAsync(blobFileStream, ct)`. Log at `Information`:
   - On match: `"Dropbox accepted blob for {ProviderId}; id={FileId}, size={Size}, contentHash matches local."`
   - On mismatch: log at `Warning` — but the upload is NOT failed (per Discrepancy 1). The GCM tag at download-time is the integrity decision.
   - **Caveat:** the provider does NOT have access to the staged blob's local path. `RangeUploader` does, but the contract doesn't pass it through. **Resolution for V1:** skip the local-side computation entirely. Log only the Dropbox-side `ContentHash` and `Size`:
     ```
     [INF] Dropbox accepted blob for {ProviderId}; id={FileId}, size={Size}, contentHash={ContentHash}.
     ```
     The `DropboxContentHash` helper is still shipped because Phase 5 will need it for cross-tail verification once the capability interface is designed. Tests cover the helper independently.
6. Return `Result<string>.Ok(metadata.Id)`.

**Failure paths:**

- `OperationCanceledException` → `Cancelled`.
- `ApiException<UploadSessionFinishError>` filtered on `AsLookupFailed` (carrying `UploadSessionLookupError` with `AsNotFound`) → `UploadSessionExpired`.
- `ApiException<UploadSessionFinishError>` filtered on `AsPath` (path conflict in `Overwrite` mode is unusual) → `UploadFailed` with metadata.
- `AuthException` → `TokenRefreshFailed`.
- `RateLimitException` → `ProviderRateLimited`.
- `HttpRequestException` → `ProviderUnreachable`.
- `Exception` → `Unknown`.

### `DropboxProvider.AbortUploadAsync(UploadSession session, CancellationToken ct)`

Per Discrepancy 3: observe `ct` at entry; log at `Debug` ("Dropbox AbortUpload requested for session {SessionId}; no-op (Dropbox has no abort endpoint)."); return `Result.Ok()`. Sessions expire naturally after 48 h.

### `DropboxProvider.DownloadAsync(string remoteId, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate `remoteId` non-empty.
2. `var dl = await _bundle.Client.Files.DownloadAsync(path: remoteId, rev: null);` — Dropbox's `path` parameter accepts both human-readable paths and `id:xxx` opaque IDs (which is what `FinaliseUploadAsync` returns).
3. `var stream = await dl.GetContentAsStreamAsync();` — returns a content stream.
4. Wrap in a `DownloadStreamWrapper` (sibling to Drive's `ResponseOwningStream`) that disposes the `IDownloadResponse<FileMetadata>` when the stream is closed.
5. Return `Result<Stream>.Ok(wrapped)`.

**Failure paths:**

- `OperationCanceledException` → `Cancelled` (dispose any partially-acquired download response first).
- `ApiException<DownloadError>` filtered on `AsPath` with `LookupError.AsNotFound` → `BlobNotFound`.
- `AuthException` → `TokenRefreshFailed`.
- `HttpRequestException` → `ProviderUnreachable`.
- `Exception` → `Unknown`.

### `DropboxProvider.DeleteAsync(string remoteId, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate `remoteId` non-empty.
2. `await _bundle.Client.Files.DeleteV2Async(path: remoteId, parentRev: null);`.
3. Return `Result.Ok()`.

**Failure paths:**

- `OperationCanceledException` → `Cancelled`.
- `ApiException<DeleteError>` filtered on `AsPathLookup` with `LookupError.AsNotFound` → `Result.Ok()` (idempotent — missing object is success per the contract).
- `AuthException` → `TokenRefreshFailed`.
- `HttpRequestException` → `ProviderUnreachable`.
- `Exception` → `Unknown`.

### `DropboxProvider.ExistsAsync(string remoteId, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate.
2. `var meta = await _bundle.Client.Files.GetMetadataAsync(path: remoteId, includeMediaInfo: false, includeDeleted: false, includeHasExplicitSharedMembers: false, includePropertyGroups: null);`.
3. If `meta` is non-null and not the deleted variant → `Result<bool>.Ok(true)`.

**Failure paths:**

- `OperationCanceledException` → `Cancelled`.
- `ApiException<GetMetadataError>` filtered on `AsPath` with `LookupError.AsNotFound` → `Result<bool>.Ok(false)`.
- `AuthException` → `TokenRefreshFailed`.
- `HttpRequestException` → `ProviderUnreachable`.
- `Exception` → `Unknown`.

### `DropboxProvider.ListAsync(string prefix, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()`.
2. Compute the listing path: if `prefix` is empty or `/`, list the root (`_rootPath`). Otherwise list `_rootPath + (prefix starts with / ? prefix : "/" + prefix)`. Dropbox listings are folder-scoped, not prefix-scoped — for partial-name prefixes that span folder boundaries we post-filter.
3. Compute the longest folder-only prefix of the supplied `prefix` (strip everything after the final `/`); list that folder.
4. `var page = await _bundle.Client.Files.ListFolderAsync(path: folderPath, recursive: true, ...);` — recursive so we pick up `_brain/{ts}.bin` etc. under the root.
5. Loop with `ListFolderContinueAsync(page.Cursor)` until `page.HasMore == false`.
6. Collect `Metadata.AsFile.Id` for entries whose path starts with the originally-supplied prefix (relative to root).
7. Return `Result<IReadOnlyList<string>>.Ok(remoteIds)`.

**Failure paths:** standard catch table.

### `DropboxProvider.CheckHealthAsync(CancellationToken ct)`

1. `Stopwatch.StartNew()`. `ct.ThrowIfCancellationRequested()`.
2. `await _bundle.Client.Users.GetCurrentAccountAsync()` — a cheap probe that exercises the auth/network path.
3. On success: return `Healthy` with elapsed latency.
4. On `AuthException`: return `AuthFailed` with detail.
5. On `RateLimitException`: return `Degraded` with detail (rate limited, not really down).
6. On `HttpRequestException`: return `Unreachable`.
7. On `Exception`: `Result<ProviderHealth>.Fail(Unknown, ...)`.

### `DropboxProvider.GetUsedBytesAsync` / `GetQuotaBytesAsync`

Single underlying call:

```csharp
private async Task<Result<SpaceUsage>> TryGetSpaceUsageAsync(CancellationToken ct);
```

That calls `_bundle.Client.Users.GetSpaceUsageAsync()` and maps exceptions. `SpaceUsage.Used` (ulong) → `(long)usage.Used` for `GetUsedBytesAsync`. `SpaceUsage.Allocation` is a polymorphic type: `AsIndividual.Allocated` for individual accounts; for team accounts read `AsTeam.Allocated`; for `AsOther` return `null`. Cast to `long?` for `GetQuotaBytesAsync`.

### `DropboxProvider.DisposeAsync()`

Idempotent via `Interlocked.Exchange(ref _disposed, 1)`. Disposes the `DropboxClientBundle` which disposes the SDK client and the HTTP client. Returns `ValueTask.CompletedTask`.

### `DropboxSetup.GetAuthorizationUriAsync(redirectUri, codeChallenge, credentials, ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate `redirectUri`, `codeChallenge`, `credentials.ClientId` non-empty.
2. `var uri = DropboxOAuth2Helper.GetAuthorizeUri(`
   - `oauthResponseType: OAuthResponseType.Code,`
   - `clientId: credentials.ClientId,`
   - `redirectUri: new Uri(redirectUri),`
   - `state: null,`
   - `forceReapprove: false,`
   - `disableSignup: false,`
   - `requireRole: null,`
   - `forceReauthentication: false,`
   - `tokenAccessType: TokenAccessType.Offline,  // analogous to Drive's access_type=offline — required to receive a refresh token`
   - `scopeList: Scopes,                          // ["files.content.write", "files.content.read", "account_info.read"]`
   - `includeGrantedScopes: IncludeGrantedScopes.None,`
   - `codeChallenge: codeChallenge);`
3. Return `Result<Uri>.Ok(uri)`.

No `prompt=consent` equivalent in the Dropbox SDK helper — Dropbox always re-issues a refresh token when `TokenAccessType.Offline` is requested (the SDK normalises this internally).

### `DropboxSetup.ExchangeCodeAsync(code, codeVerifier, redirectUri, credentials, dek, ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate inputs (`code`, `codeVerifier`, `redirectUri`, `credentials.ClientId`, `credentials.ClientSecret` all non-empty).
2. Call the SDK static helper:
   ```csharp
   var response = await DropboxOAuth2Helper.ProcessCodeFlowAsync(
       code: code,
       appKey: credentials.ClientId,
       appSecret: credentials.ClientSecret,
       redirectUri: redirectUri,
       client: _oauthHttpClient,
       codeVerifier: codeVerifier).ConfigureAwait(false);
   ```
3. Validate `response.RefreshToken` is non-empty. On missing → `Result<byte[]>.Fail(ProviderApiChanged, "Dropbox token endpoint did not return a refresh token; verify TokenAccessType.Offline was sent.")`.
4. Encrypt: `var envelope = ProviderTokenCrypto.Encrypt(response.RefreshToken, dek);`. Return `Result<byte[]>.Ok(envelope)`.

**Failure paths:**

- `OperationCanceledException` → `Cancelled`.
- `AuthException` → `ProviderAuthFailed` ("Dropbox rejected the OAuth code or client credentials.").
- `HttpRequestException` → `ProviderUnreachable`.
- `Exception` (e.g. JSON parse failures from inside the SDK helper) → `Unknown` with no body in the message (the SDK exception's message is opaque enough to log).

### `DropboxSetup.ValidatePathAsync(path, skinkRoot, ct)`

Returns `Result.Ok(ValidationResult.Invalid("Dropbox is an OAuth provider; no local path is required."))`. Synchronous via `Task.FromResult`.

### `DropboxSetup.CreateProviderAsync(providerId, displayName, encryptedToken, credentials, providerConfigJson, dek, ct)`

1. `ct.ThrowIfCancellationRequested()`. Validate `providerId`, `displayName`, `credentials.ClientId`, `credentials.ClientSecret`, `encryptedToken` non-null/non-empty.
2. Decrypt the refresh token via `ProviderTokenCrypto.TryDecrypt`; on failure → `Result<IStorageProvider>.Fail(TokenRevoked, "Could not decrypt Dropbox refresh token; the brain may be corrupt or the DEK is wrong.")`.
3. Resolve `persistedRootPath`:
   - If `providerConfigJson` is non-empty and parses as `DropboxProviderConfig` with non-empty `RootPath` → use that.
   - Else → `null` (the helper below defaults to `"/" + ProviderConstants.CloudRootFolderName`).
4. Delegate to `CreateProviderFromConfigAsync(providerId, displayName, credentials.ClientId, credentials.ClientSecret, refreshToken, persistedRootPath, ct)`.

`CreateProviderFromConfigAsync`:
1. Call `_clientFactory.Create(appKey, appSecret, refreshToken, _loggerFactory)`; on failure → propagate.
2. Resolve the root path:
   - If `persistedRootPath` is non-null → use it directly.
   - Else → `var rootPath = "/" + ProviderConstants.CloudRootFolderName;` and continue.
3. Construct `DropboxProvider(providerId, displayName, bundle, rootPath, _loggerFactory.CreateLogger<DropboxProvider>())`.
4. Return `Result<IStorageProvider>.Ok(provider)`.

Catch block disposes the bundle on failure.

Note: unlike `GoogleDriveSetup`, Dropbox does NOT need a "lookup or create root folder" step. Dropbox folders are auto-created on first upload (the SDK creates intermediate path components implicitly). The `RootPath` is just a string and is persisted into `Providers.ProviderConfig` by §4.6's `AddTailAsync` after the first successful upload, but works without persistence too (the second open recreates the same string).

### `BrainBackedProviderRegistry.TryBuildDropboxAdapterAsync(row, dek, factory, loggerFactory, logger, ct)`

```csharp
private static async Task<IStorageProvider?> TryBuildDropboxAdapterAsync(
    ProviderRow row,
    ReadOnlyMemory<byte> dek,
    IDropboxClientFactory dropboxFactory,
    ILoggerFactory loggerFactory,
    ILogger<BrainBackedProviderRegistry> logger,
    CancellationToken ct)
{
    // 1. Validate BYOC bits — same shape as TryBuildGoogleDriveAdapterAsync.
    if (string.IsNullOrWhiteSpace(row.ClientID)) { logger.LogWarning(...); return null; }
    if (row.EncryptedToken is null || row.EncryptedToken.Length == 0) { ... return null; }
    if (row.EncryptedClientSecret is null || row.EncryptedClientSecret.Length == 0) { ... return null; }

    // 2. Decrypt client secret + refresh token.
    if (!ProviderTokenCrypto.TryDecrypt(row.EncryptedClientSecret, dek, out var clientSecret)) { ... return null; }
    if (!ProviderTokenCrypto.TryDecrypt(row.EncryptedToken, dek, out var refreshToken)) { ... return null; }

    // 3. Parse the (optional) persisted root path.
    string? rootPath = null;
    if (!string.IsNullOrWhiteSpace(row.ProviderConfig))
    {
        try
        {
            var cfg = JsonSerializer.Deserialize(row.ProviderConfig,
                DropboxProviderConfigJsonContext.Default.DropboxProviderConfig);
            rootPath = cfg?.RootPath;
        }
        catch (JsonException ex) { logger.LogWarning(ex, ...); return null; }
    }

    // 4. Construct via the setup's reusable helper. We pass our own HttpClient for OAuth here
    //    because the setup's two HttpClients (factory's bundle + token-exchange) are independent.
    using var oauthClient = new HttpClient(); // unused on CreateProviderFromConfigAsync path; mirror of Drive pattern.
    var setup = new DropboxSetup(dropboxFactory, oauthClient, loggerFactory);

    var result = await setup.CreateProviderFromConfigAsync(
        row.ProviderID, row.DisplayName, row.ClientID, clientSecret, refreshToken, rootPath, ct).ConfigureAwait(false);

    if (!result.Success) { logger.LogWarning(...); return null; }
    return result.Value!;
}
```

Modify the existing `TryBuildAdapterAsync` switch to dispatch `"dropbox"` → `TryBuildDropboxAdapterAsync` (currently logs a warning and returns null). Update the `CreateAsync` signatures: the public overload stays unchanged externally; the internal overload that takes `IGoogleDriveClientFactory` becomes also-takes `IDropboxClientFactory`. The existing internal one-factory overload remains as a back-compat shim that forwards with `new DropboxClientFactory()`, so §4.3 tests don't need to change.

---

## Integration points

- `IStorageProvider` / `UploadSession` / `ProviderHealth` / `ProviderHealthStatus` (existing public contracts) — implemented unchanged.
- `IProviderSetup` / `ProviderCredentials` / `ProviderSetupKind` / `ValidationResult` (from §4.1) — implemented unchanged.
- `IOAuthCaptureFlow` (from §4.1/§4.2) — **not consumed inside §4.4**. The setup interface receives an authorisation `code` as input; the CLI (§4.6) is the consumer of `IOAuthCaptureFlow`.
- `ProviderTokenCrypto` (from §4.1) — consumed in `DropboxSetup.ExchangeCodeAsync` and `BrainBackedProviderRegistry.TryBuildDropboxAdapterAsync` and `DropboxSetup.CreateProviderAsync`.
- `ProviderConstants.CloudRootFolderName` — read by `DropboxSetup.CreateProviderFromConfigAsync` for the default root path.
- `BrainBackedProviderRegistry` (from §4.1 / §4.3) — modified per "Files to modify".
- `RangeUploader.FinaliseAndVerifyAsync` — **unchanged**. `DropboxProvider` does not implement `ISupportsRemoteHashCheck`; the existing "trust the GCM tag" branch handles Dropbox blobs (Discrepancy 1).
- `Dropbox.Api` (NuGet, version 7.0.0) — consumed via `DropboxClient`, `DropboxClientConfig`, `DropboxOAuth2Helper`, `OAuth2Response`, `TokenAccessType`, `OAuthResponseType`, `IncludeGrantedScopes`, `FilesUserRoutes`, `UsersUserRoutes`, `UploadSessionCursor`, `UploadSessionStartResult`, `CommitInfo`, `WriteMode`, `FileMetadata`, `SpaceUsage`, `ApiException<TError>`, `AuthException`, `RateLimitException`, `Files.UploadSessionAppendError`, `Files.UploadSessionLookupError`, `Files.UploadSessionFinishError`, `Files.DeleteError`, `Files.DownloadError`, `Files.GetMetadataError`, `Files.LookupError`.

---

## Principles touched

- **Principle 1** (Core never throws across public API boundary) — every public method on `DropboxProvider`, `DropboxSetup`, `DropboxClientFactory`, `DropboxContentHash` returns `Result` / `Result<T>` / direct values. Exceptions caught and wrapped at the public boundary.
- **Principle 6** (zero-knowledge at external boundaries) — Dropbox only ever sees AES-GCM encrypted bytes uploaded by `RangeUploader`. Refresh token and client secret are DEK-encrypted at rest in the brain. Cleartext credentials exist only inside `DropboxClient` (an unavoidable SDK design) for the volume's lifetime; the client is `Dispose`d on volume close.
- **Principle 12** (OS-agnostic) — all Dropbox operations work identically on Windows/Linux/macOS. The Dropbox SDK targets `netstandard2.0`/`netstandard2.1` and runs on every supported RID.
- **Principle 13** (`CancellationToken ct` last) — every new async method takes `ct` as the final parameter.
- **Principle 14** (`OperationCanceledException` first catch) — every async method's catch chain starts with `OperationCanceledException`.
- **Principle 15** (no bare `catch (Exception)` as sole catch) — every catch block has specific types before the fallback.
- **Principle 17** (`CancellationToken.None` literal in compensation) — `AbortUploadAsync` is a no-op (no compensation work). `DisposeAsync` does not accept `ct`. No other compensation paths.
- **Principle 23** (provider contract frozen) — no edits to `IStorageProvider`, `UploadSession`, `ProviderHealth`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ValidationResult`. No new capability interfaces introduced in §4.4 (Discrepancy 1).
- **Principle 24** (no silent background failures) — Dropbox's auth/refresh failures surface as `Result.Fail(TokenRefreshFailed)`. The provider does not publish to `INotificationBus`; `UploadQueueService` is the publisher per Principle 24.
- **Principle 26** (no secrets in logs) — `DropboxProvider` and `DropboxSetup` never log `refreshToken`, `clientSecret`, `accessToken`, `code`, `codeVerifier`. The OAuth code-exchange HTTP request/response is not logged (the SDK does its own logging which we don't connect to MEL). The structured-log scope on every provider operation includes `ProviderID` and the encoded `SessionUri`; the `SessionUri` envelope contains the SessionId (opaque, not a secret) and the remote path (not a secret — it's on the user's Dropbox).
- **Principle 28** (Core depends only on MEL abstractions) — every logger in §4.4 is `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions`. The Dropbox SDK has its own internal logging but no hook into `ILogger<T>`; we don't connect them.
- **Principle 31** (keys zeroed on volume close) — the `refreshToken` and `clientSecret` cleartext strings are minimised in scope. They cannot be zeroed (`string` is interned and immutable); this is the unavoidable .NET constraint that also applies to §4.3. The `DropboxClient` holds the cleartext tokens internally; it is disposed on volume close via the bundle.
- **Principle 32** (no telemetry, no update checks, no network chatter) — the only network endpoints touched are Dropbox's documented API endpoints (`api.dropboxapi.com`, `content.dropboxapi.com`). No analytics, no metrics export, no version-check.

---

## Test spec

### `tests/FlashSkink.Tests/Providers/Dropbox/RecordingHttpMessageHandler.cs`

Duplicated from `tests/FlashSkink.Tests/Providers/GoogleDrive/RecordingHttpMessageHandler.cs` per Discrepancy 2. Namespace `FlashSkink.Tests.Providers.Dropbox`. Identical surface: `Setup(method, urlPrefix, responder)`, `Enqueue(...)`, `IReadOnlyList<RecordedRequest> ReceivedRequests`, `RequestCount`. The `CannedResponses.Status` / `WithJson` / `WithBody` helpers are also duplicated as part of the file. The Drive-specific helpers (`ResumableInit`, `Range308`, `Empty308`) are not carried over.

### `tests/FlashSkink.Tests/Providers/Dropbox/DropboxCannedResponses.cs`

Dropbox-shaped helpers:

- `OAuthTokenResponse(string refreshToken, string accessToken = "at-x", int expiresIn = 14400)` — returns a `200 OK` JSON body matching `OAuth2Response`'s wire shape (`{"access_token":"...","token_type":"bearer","refresh_token":"...","expires_in":14400,"uid":"u1","scope":"..."}`).
- `OAuthInvalidGrant()` — `400` with `{"error":"invalid_grant","error_description":"..."}`.
- `UploadSessionStartResult(string sessionId)` — `200` with `{"session_id":"<sid>"}`.
- `UploadSessionAppendV2Ok()` — `200` with empty body.
- `UploadSessionFinishResult(string id, ulong size, string contentHash, string path)` — `200` with file-metadata JSON.
- `ApiError(int httpStatus, string errorTag, string summary)` — Dropbox's standard error envelope.
- `AuthError(string errorTag = "expired_access_token")` — `401`.
- `IncorrectOffsetAppendError(ulong correctOffset)` — `409` (Dropbox's error code for `incorrect_offset`) with the appropriate envelope.

### `tests/FlashSkink.Tests/Providers/Dropbox/FakeDropboxClientFactory.cs`

`IDropboxClientFactory` test double that constructs a real `DropboxClient` initialised with `DropboxClientConfig.HttpClient = new HttpClient(RecorderHandler)`. Captures the `(appKey, appSecret, refreshToken)` passed in. Exposes `RecordingHttpMessageHandler Handler { get; }` for response setup.

### `tests/FlashSkink.Tests/Providers/Dropbox/DropboxProviderTests.cs` — class `DropboxProviderTests`

Constructs `DropboxProvider` via a private `Build()` helper. Tests:

**Helpers / metadata**
- `Metadata_HasExpectedValues` — `ProviderType == "dropbox"`, `RootPath` matches what was passed.
- `DropboxSessionUri_RoundTrip` — encode + decode preserves both fields. Malformed input → `TryDecode` returns false.

**BeginUploadAsync**
- `BeginUploadAsync_HappyPath_ReturnsEncodedSessionWithExpiry` — handler returns canned `UploadSessionStartResult`. Assert `session.SessionUri` decodes to `("sid-abc", "/FlashSkink Backup/blob.bin")`; `session.ExpiresAt` ≥ `UtcNow + 47h`.
- `BeginUploadAsync_RemoteNameWithLeadingSlash_NotDoubledUp`.
- `BeginUploadAsync_RemoteNameWithSubPath_BuildsCorrectDestination` — `"_brain/2026-05.bin"` → `"/FlashSkink Backup/_brain/2026-05.bin"`.
- `BeginUploadAsync_AuthException_ReturnsTokenRefreshFailed` — handler returns 401 with auth-error body.
- `BeginUploadAsync_NetworkFailure_ReturnsProviderUnreachable` — handler throws `HttpRequestException`.
- `BeginUploadAsync_Cancelled_ReturnsCancelled`.
- `BeginUploadAsync_InvalidArgument_NegativeBytes`.
- `BeginUploadAsync_InvalidArgument_EmptyName`.

**GetUploadedBytesAsync**
- `GetUploadedBytesAsync_ReturnsSessionLocalState_NoNetwork` — verifies `factory.Handler.RequestCount == 0` after the call.
- `GetUploadedBytesAsync_Cancelled_ReturnsCancelled`.

**UploadRangeAsync**
- `UploadRangeAsync_HappyPath_ReturnsOk` — handler returns `200`. Assert `Ok()` and the cursor in the request body matches `(sessionId, offset)`.
- `UploadRangeAsync_IncorrectOffset_ReturnsUploadSessionExpired` — handler returns Dropbox `incorrect_offset` error.
- `UploadRangeAsync_SessionNotFound_ReturnsUploadSessionExpired`.
- `UploadRangeAsync_SessionClosed_ReturnsUploadSessionExpired`.
- `UploadRangeAsync_AuthException_ReturnsTokenRefreshFailed`.
- `UploadRangeAsync_RateLimit_ReturnsProviderRateLimited`.
- `UploadRangeAsync_Network_ReturnsProviderUnreachable`.
- `UploadRangeAsync_Cancelled_ReturnsCancelled`.
- `UploadRangeAsync_MalformedSessionUri_ReturnsUploadSessionExpired` — pass a session with `SessionUri = "garbage"`.
- `UploadRangeAsync_InvalidArgument_NegativeOffset` / `_EmptyData` / `_OverBounds`.

**FinaliseUploadAsync**
- `FinaliseUploadAsync_HappyPath_ReturnsFileId` — handler returns metadata. Assert returned remoteId == `"id:abc123"`.
- `FinaliseUploadAsync_LookupFailedNotFound_ReturnsUploadSessionExpired`.
- `FinaliseUploadAsync_AuthException_ReturnsTokenRefreshFailed`.
- `FinaliseUploadAsync_LogsContentHashAndSize` — capture logger output; assert it contains `id=`, `size=`, `contentHash=`.
- `FinaliseUploadAsync_Cancelled_ReturnsCancelled`.
- `FinaliseUploadAsync_MalformedSessionUri_ReturnsUploadSessionExpired`.

**AbortUploadAsync**
- `AbortUploadAsync_NoOp_ReturnsOk` — assert `Ok()`, assert `factory.Handler.RequestCount == 0`.
- `AbortUploadAsync_Cancelled_ReturnsCancelled`.

**DownloadAsync**
- `DownloadAsync_HappyPath_ReturnsStream` — assert the bytes read from the stream match the canned body.
- `DownloadAsync_NotFound_ReturnsBlobNotFound`.
- `DownloadAsync_AuthException_ReturnsTokenRefreshFailed`.
- `DownloadAsync_Cancelled_ReturnsCancelled` — disposing partial response.
- `DownloadAsync_StreamDispose_DisposesUnderlyingResponse` — assert the wrapped response is disposed once.

**DeleteAsync**
- `DeleteAsync_HappyPath_ReturnsOk`.
- `DeleteAsync_NotFound_Idempotent_ReturnsOk`.
- `DeleteAsync_AuthException_ReturnsTokenRefreshFailed`.

**ExistsAsync**
- `ExistsAsync_Exists_ReturnsTrue`.
- `ExistsAsync_NotFound_ReturnsFalse`.

**ListAsync**
- `ListAsync_EmptyPrefix_ListsAllUnderRoot`.
- `ListAsync_PrefixMatchesSubfolder_FiltersPostList`.
- `ListAsync_Pagination_FollowsCursor` — first page has `HasMore=true`; second page is requested via `ListFolderContinueAsync` and returns the rest.

**CheckHealthAsync**
- `CheckHealthAsync_HappyPath_ReturnsHealthyWithLatency`.
- `CheckHealthAsync_AuthException_ReturnsAuthFailed`.
- `CheckHealthAsync_Network_ReturnsUnreachable`.
- `CheckHealthAsync_RateLimit_ReturnsDegraded`.

**GetUsedBytesAsync / GetQuotaBytesAsync**
- `GetUsedBytes_ReadsSpaceUsageUsed`.
- `GetQuotaBytes_IndividualAllocation_ReturnsAllocated`.
- `GetQuotaBytes_TeamAllocation_ReturnsAllocated`.
- `GetQuotaBytes_OtherAllocation_ReturnsNull`.

**DisposeAsync**
- `DisposeAsync_Idempotent_DisposesBundleOnce`.

**Capability**
- `Provider_DoesNotImplement_ISupportsRemoteHashCheck` — captures Discrepancy 1 as a test so future revisions are explicit.

### `tests/FlashSkink.Tests/Providers/Dropbox/DropboxSetupTests.cs` — class `DropboxSetupTests`

- `Metadata_HasExpectedValues` — `ProviderType == "dropbox"`, `DisplayName == "Dropbox"`, `SetupKind == OAuth`.
- `GetAuthorizationUriAsync_BuildsCorrectUrl` — assert URL host is Dropbox's authorize endpoint and query includes `code_challenge`, `code_challenge_method=S256`, `token_access_type=offline`, scope list.
- `GetAuthorizationUriAsync_MissingClientId_ReturnsInvalidArgument`.
- `GetAuthorizationUriAsync_MissingRedirectUri_ReturnsInvalidArgument`.
- `GetAuthorizationUriAsync_MissingCodeChallenge_ReturnsInvalidArgument`.
- `ExchangeCodeAsync_HappyPath_ReturnsEncryptedRefreshToken` — handler returns canned OAuth response. Decrypt result with the test DEK, assert plaintext matches.
- `ExchangeCodeAsync_RequestBodyContainsCodeAndVerifier`.
- `ExchangeCodeAsync_MissingRefreshToken_ReturnsProviderApiChanged`.
- `ExchangeCodeAsync_InvalidGrant_ReturnsProviderAuthFailed`.
- `ExchangeCodeAsync_NetworkFailure_ReturnsProviderUnreachable`.
- `ExchangeCodeAsync_Cancelled_ReturnsCancelled`.
- `ExchangeCodeAsync_MissingCredentials_ReturnsInvalidArgument` — both ClientId and ClientSecret.
- `ValidatePathAsync_ReturnsInvalid` — Dropbox is OAuth.
- `CreateProviderAsync_HappyPath_WithConfig_UsesPersistedRootPath` — `providerConfigJson` carries `rootPath: "/Custom"`. Assert constructed provider's `RootPath == "/Custom"`.
- `CreateProviderAsync_HappyPath_WithoutConfig_UsesDefaultRootPath`.
- `CreateProviderAsync_DecryptFails_ReturnsTokenRevoked` — envelope encrypted with a different DEK.
- `CreateProviderAsync_MissingCredentials_ReturnsInvalidArgument`.
- `CreateProviderAsync_MalformedConfigJson_ReturnsInvalidArgument`.

### `tests/FlashSkink.Tests/Providers/Dropbox/DropboxContentHashTests.cs`

- `ComputeAsync_EmptyStream_ReturnsSha256OfEmpty` — known value: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
- `ComputeAsync_SingleByte_MatchesKnownVector` — pre-computed expected value.
- `ComputeAsync_ExactBlockSize_MatchesKnownVector` — 4 MiB of `0xAB`.
- `ComputeAsync_OverOneBlock_MatchesKnownVector` — 4 MiB + 1 byte of `0xCD`.
- `ComputeAsync_MultiBlock_MatchesKnownVector` — 12 MiB of pattern.
- `ComputeAsync_Cancelled_ReturnsEmpty` — supply a cancelled token.
- `ComputeAsync_NonSeekableStream_Works` — wrap input in a stream that disallows `Seek` to confirm we don't rely on it.

Known vectors can be computed offline once during plan finalisation; we won't depend on a third-party tool — for V1 we'll generate them by running the algorithm itself against a freshly-built reference and pinning the output (a tautology in the literal sense, but the *algorithm* being correct is the assertion we make in the implementation review). The empty-stream and single-byte vectors are independently verifiable against Dropbox's published algorithm description.

### `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` (extend, not duplicate)

Add five new test methods (mirror §4.3 Drive tests):

- `CreateAsync_OneDropboxRow_WithAllCredentials_ConstructsProvider`.
- `CreateAsync_OneDropboxRow_MissingEncryptedToken_LogsWarningAndSkips`.
- `CreateAsync_OneDropboxRow_MissingEncryptedClientSecret_LogsWarningAndSkips`.
- `CreateAsync_OneDropboxRow_DecryptClientSecretFails_LogsWarningAndSkips`.
- `CreateAsync_MixedRows_FileSystemAndGoogleDriveAndDropbox_AllLoaded`.

The tests use the new `IDropboxClientFactory` test double via the extended internal `CreateAsync` overload.

---

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (Linux, Windows, macOS Intel, macOS ARM64).
- [ ] All Phase 0–4.3 tests continue to pass.
- [ ] All new tests pass.
- [ ] `DropboxProvider`, `DropboxSetup`, `IDropboxClientFactory`, `DropboxClientFactory`, `DropboxClientBundle`, `DropboxProviderConfig`, `DropboxContentHash` exist in `src/FlashSkink.Core/Providers/Dropbox/`.
- [ ] `BrainBackedProviderRegistry` resolves `"dropbox"` rows to live providers when supplied a working `IDropboxClientFactory`.
- [ ] `RangeUploader` is unchanged in source.
- [ ] `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ProviderHealth`, `ISupportsRemoteHashCheck` are unchanged in source.
- [ ] No new `ErrorCode` values added.
- [ ] No reference to any UI framework anywhere in the new code (Principles 8–11).
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] `Dropbox.Api 7.0.0` package version is correct in `Directory.Packages.props`; `Dropbox.Api` package reference exists in `FlashSkink.Core.csproj`.
- [ ] No string interpolation of secret-named fields in any logger call.
- [ ] No tests touch the real Dropbox API (all SDK calls are routed through `RecordingHttpMessageHandler`).
- [ ] `DropboxProvider.SessionUri` envelope round-trips correctly for all the lifecycle methods (verified by tests).
- [ ] §4.3 Drive tests still build and pass after the `BrainBackedProviderRegistry.CreateAsync` internal overload signature extension.

---

## Line-of-code budget

| File | Lines |
|---|---|
| `Core/Providers/Dropbox/DropboxProvider.cs` | ~520 |
| `Core/Providers/Dropbox/DropboxSetup.cs` | ~320 |
| `Core/Providers/Dropbox/IDropboxClientFactory.cs` | ~30 |
| `Core/Providers/Dropbox/DropboxClientFactory.cs` | ~60 |
| `Core/Providers/Dropbox/DropboxClientBundle.cs` | ~40 |
| `Core/Providers/Dropbox/DropboxProviderConfig.cs` | ~50 |
| `Core/Providers/Dropbox/DropboxContentHash.cs` | ~80 |
| `Core/Providers/BrainBackedProviderRegistry.cs` (delta) | ~110 |
| `Directory.Packages.props` (delta) | ~1 |
| `FlashSkink.Core.csproj` (delta) | ~1 |
| **src delta** | **~1,210** |
| `tests/.../Dropbox/DropboxProviderTests.cs` | ~620 |
| `tests/.../Dropbox/DropboxSetupTests.cs` | ~360 |
| `tests/.../Dropbox/DropboxContentHashTests.cs` | ~100 |
| `tests/.../Dropbox/FakeDropboxClientFactory.cs` | ~80 |
| `tests/.../Dropbox/RecordingHttpMessageHandler.cs` | ~170 |
| `tests/.../Dropbox/DropboxCannedResponses.cs` | ~90 |
| `tests/.../Providers/BrainBackedProviderRegistryTests.cs` (delta) | ~180 |
| **test delta** | **~1,600** |
| **Total delta** | **~2,810** |

---

## Non-goals for §4.4

- Do NOT implement `ISupportsRemoteHashCheck` for `DropboxProvider`. Dropbox uses a SHA-256-based content-hash, not XXHash64 (Discrepancy 1). Cross-tail verification with provider-specific algorithms lands in Phase 5.
- Do NOT modify `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ProviderHealth`, `ISupportsRemoteHashCheck` (Principle 23 — frozen).
- Do NOT modify `RangeUploader`, `UploadQueueService`, `BrainMirrorService` (cross-cutting decision 6).
- Do NOT introduce a new `ErrorCode` value. The Dropbox failure-mode mappings reuse `ProviderUnreachable`, `ProviderQuotaExceeded`, `ProviderRateLimited`, `ProviderAuthFailed`, `TokenRefreshFailed`, `TokenRevoked`, `UploadFailed`, `UploadSessionExpired`, `BlobNotFound`, `ProviderApiChanged`, `Cancelled`, `InvalidArgument`, `Unknown`.
- Do NOT implement the CLI commands `setup add` / `setup remove` / `setup list` — those are §4.6.
- Do NOT implement multi-account configurations within a single provider type. V1 allows one tail per provider type.
- Do NOT implement automated provider-console setup (creating the Dropbox app on the user's behalf). That is the paid layer (Blueprint §24.1).
- Do NOT add `INotificationBus` publish calls inside `DropboxProvider`. Per Principle 24 the publisher is `UploadQueueService`.
- Do NOT wire `IOAuthCaptureFlow` into `DropboxSetup`. The setup's role is to build the authorisation URL and exchange the code; loopback capture is the CLI's responsibility (§4.6).
- Do NOT flatten the remote name path (unlike Drive). Dropbox supports subfolders natively and creates intermediate path components implicitly on upload.
- Do NOT extract `RecordingHttpMessageHandler` to a shared test namespace in this PR (Discrepancy 2). That refactor lands when §4.5 OneDrive adds the third user.
- Do NOT compute the local-side Dropbox `ContentHash` inside `DropboxProvider.FinaliseUploadAsync`. The provider does not have the staged blob's path (the `IStorageProvider` contract does not pass it). The provider logs only the Dropbox-side hash + size; the `DropboxContentHash` helper is shipped for Phase 5 consumption.
- Do NOT rewire the existing `_witness/` / `_brain/` string literals in `WitnessStore` and `BrainMirrorService` to `ProviderConstants`. That refactor is a non-goal of §4.4 as it was of §4.3.

---

## Open questions for Gate 1 review

1. **Discrepancy 0 — corrected NuGet pin.** Plan commits to changing `Dropbox.Api 9.0.0` (non-existent) to `7.0.0` (latest published, `net10.0`-compatible). Accept, or attempt to source a `9.0.0` from somewhere else (no such source exists publicly)?
2. **Discrepancy 1 — verification path for Dropbox.** Plan commits to Option A (skip `ISupportsRemoteHashCheck`; trust GCM tag; log Dropbox-side hash + size at `Information` for observability). Same shape as §4.3's resolution. Accept?
3. **Discrepancy 2 — duplicate `RecordingHttpMessageHandler` rather than extract.** Plan commits to duplication for §4.4 with extract-to-shared deferred to §4.5. Accept, or extract now?
4. **Discrepancy 3 — `AbortUploadAsync` no-op.** Plan commits to logging at `Debug` and returning `Ok()` without contacting Dropbox. Accept?
5. **Discrepancy 4 — `GetUploadedBytesAsync` returns local state.** Plan commits to Option A (return `session.BytesUploaded` without a network probe). Accept, or pursue Option B (zero-byte append probe)?
6. **Discrepancy 5 — `IncorrectOffset` → `UploadSessionExpired`.** Plan commits to full restart on offset drift. Accept, or pursue an interface change to communicate the corrected offset (forbidden by Principle 23 — so this is informational only)?
7. **Discrepancy 6 — Hand `HttpClient` to the SDK's static `ProcessCodeFlowAsync`.** Plan commits to using the SDK's static helper rather than hand-rolling the POST. Accept, or hand-roll like §4.3 for stylistic consistency at the cost of duplicating SDK logic?
8. **Discrepancy 7 — Encode `(sessionId, remotePath)` into `UploadSession.SessionUri` as JSON.** The only path-shaped storage available on the contract. Accept, or pursue an interface change (forbidden by Principle 23 — informational)?
9. **OAuth scope set: `files.content.write`, `files.content.read`, `account_info.read`.** The third is needed for `GetSpaceUsageAsync`. Accept, or trim to the minimum (which would force `GetQuotaBytesAsync` to return `null` always)?
10. **`WriteMode.Overwrite` at finalisation.** Plan commits to overwriting any existing blob with the same path (matches our intent — second upload of the same `_brain/{ts}.bin` should replace, not auto-rename). Accept?
11. **`mute: true` at finalisation.** Plan commits to suppressing the user-facing Dropbox-client notification on each blob upload (otherwise the user gets a desktop notification per file). Accept?
12. **`account_info.read` scope rationale.** Used by `CheckHealthAsync` (`GetCurrentAccountAsync`) and `GetQuotaBytesAsync` (`GetSpaceUsageAsync`). Both are required by the `IStorageProvider` contract.
