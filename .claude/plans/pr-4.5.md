# PR 4.5 — OneDrive provider and setup

**Branch:** pr/4.5-onedrive-provider
**Blueprint sections:** §10 (provider contract & V1 freeze, §10.5), §15 (resumable upload lifecycle — §15.5 range size, §15.7 post-finalise verification), §18 (key hierarchy / provider-token encryption), §24 (BYOC provider setup), §3 & §25 (appliance vocabulary)
**Dev plan section:** phase-4 §4.5

**Phase-4 cross-cutting decisions touched:** Decision 6 ("Do NOT modify `RangeUploader`, `UploadQueueService`, or `BrainMirrorService`") — honoured; see Discrepancy 1 for the one adjacent constant (`UploadConstants.RangeSize`) that Q1 puts on the table. Phase-4 acceptance "No new `ErrorCode` values added" — honoured; OneDrive maps onto the existing `ErrorCode` set only.

## Scope

Add OneDrive as the third cloud tail: a raw-HTTP `IStorageProvider` adapter driving Microsoft Graph's resumable-upload protocol (`createUploadSession` → ranged `PUT` → DriveItem), an `IProviderSetup` implementing the Microsoft identity-platform v2.0 OAuth + PKCE authorisation-code flow (MSAL-free), a client factory carrying a self-refreshing `DelegatingHandler`, and the registry wiring that replaces today's "onedrive → not yet supported" stub. OneDrive is **path-addressed** (like Dropbox, unlike Google): objects live at `root:/{rootPath}/{remoteName}:` with no folder-id resolution step. Mirrors the §4.3 (Google raw-HTTP) and §4.4 (Dropbox) shapes. The QuickXorHash algorithm is implemented locally and unit-tested but **not** consumed in the V1 upload path — shipped for Phase 5 cross-tail verification, exactly as §4.4 shipped `DropboxContentHash`.

## Discrepancies surfaced against the dev-plan text

1. **§4.5 says "Microsoft.Graph wiring"; this plan recommends raw HTTP against the Graph REST endpoints, MSAL-free.** Rationale: the resumable-upload `PUT` to the pre-authenticated `uploadUrl` must carry **no** `Authorization` header (sending one yields 401) — the Graph SDK and MSAL fight this, whereas the §4.3 Google adapter already proved the raw-HTTP + custom `DelegatingHandler` pattern works cleanly. Choosing raw HTTP lets us drop the `Microsoft.Graph` and `Microsoft.Identity.Client` pins. **Gate-1 Q2/Q8.** The entire plan below is written for the raw-HTTP path; choosing the SDK is a material rewrite.

2. **§4.5 says "Implement the QuickXorHash algorithm locally (~50 lines)."** This plan does implement it (`OneDriveQuickXorHash`) **and** unit-tests it, but — matching the §4.4 precedent where `DropboxContentHash` shipped without being wired — it is **not** invoked in the upload/verification path and the provider does **not** implement `ISupportsRemoteHashCheck`. `FinaliseUploadAsync` logs the DriveItem's server-returned hash(es) + size at `Information`, and the local hash is shipped for Phase 5 to consume. **Gate-1 Q3.**

3. **320 KiB fragment-alignment vs. the 4 MiB `UploadConstants.RangeSize`.** Graph requires every *non-final* `PUT` fragment to be an exact multiple of 320 KiB (327,680 B). `RangeSize = 4 MiB = 12.8 × 320 KiB` is **not** aligned, so any blob larger than one range (> 4 MiB) would fail Graph's commit. Google (256 KiB multiples — 4 MiB is fine) and Dropbox/FileSystem (no requirement) are unaffected; only OneDrive breaks. This is the dominant blocking decision. **Gate-1 Q1.**

## Files to create

Production — `src/FlashSkink.Core/Providers/OneDrive/`:

- `OneDriveProvider.cs` — `internal sealed partial class OneDriveProvider : IStorageProvider, IAsyncDisposable`. Raw-HTTP Graph adapter: create-session/range-PUT/finalise/abort upload lifecycle, download, delete, exists, list, health, used/quota. ~950 lines.
- `OneDriveSetup.cs` — `internal sealed partial class OneDriveSetup : IProviderSetup`. v2.0 OAuth + PKCE authorise/exchange, path validation, provider construction. ~490 lines.
- `OneDriveClientFactory.cs` — `internal sealed class OneDriveClientFactory : IOneDriveClientFactory` + `internal sealed class MicrosoftAuthDelegatingHandler : DelegatingHandler` (MSAL-free form-POST refresh on 401, token+expiry cache). ~230 lines.
- `IOneDriveClientFactory.cs` — `internal interface IOneDriveClientFactory` (single `Create(...)` returning `Result<OneDriveClientBundle>`). ~45 lines.
- `OneDriveClientBundle.cs` — `internal sealed class OneDriveClientBundle : IDisposable`. Carries the authed `HttpClient` (session/download/delete/exists/list/health) and a plain no-auth `HttpClient` (range PUT / GET status / DELETE abort against `uploadUrl`); idempotent `Dispose` (HTTP clients in order). ~60 lines.
- `OneDriveProviderConfig.cs` — `internal sealed record OneDriveProviderConfig { [JsonPropertyName("rootPath")] required string RootPath }` + source-gen `OneDriveProviderConfigJsonContext`. Mirrors `DropboxProviderConfig`. ~28 lines.
- `OneDriveQuickXorHash.cs` — `internal static class OneDriveQuickXorHash` implementing Microsoft's QuickXorHash (160-bit shift-XOR + length-XOR, base64-encoded). Streaming, pooled buffer, no `stackalloc` across `await`. Shipped for Phase 5; not consumed in V1. Mirrors `DropboxContentHash`. ~90 lines.

Tests — `tests/FlashSkink.Tests/Providers/OneDrive/`:

- `OneDriveProviderTests.cs` — full lifecycle + failure-mapping coverage via `RecordingHttpMessageHandler`. ~720 lines.
- `OneDriveSetupTests.cs` — authorise-URL construction, code exchange (success + error-tag), path validation, provider construction. ~410 lines.
- `OneDriveQuickXorHashTests.cs` — known-answer vectors, empty input, streaming-vs-buffered equivalence, cancellation. ~120 lines.
- `FakeOneDriveClientFactory.cs` — `internal sealed class` returning a bundle whose two `HttpClient`s are backed by a supplied `RecordingHttpMessageHandler` (authed) and a second handler (plain). ~90 lines.
- `OneDriveCannedResponses.cs` — `internal static class` of canned Graph JSON/status helpers (createUploadSession 200, range 202 + `nextExpectedRanges`, final 201 DriveItem, GET-status 200, DELETE 204, 401/403/404/416/429/507). ~160 lines.

Shared test-support extraction (see Q5):

- `tests/FlashSkink.Tests/Providers/RecordingHttpMessageHandler.cs` — the generic recorder, lifted to namespace `FlashSkink.Tests.Providers`, deduplicating the two per-provider copies. ~160 lines.

## Files to modify

- `src/FlashSkink.Core/Providers/BrainBackedProviderRegistry.cs` — (a) thread `IOneDriveClientFactory` through the `CreateAsync` overload chain additively (new most-internal overload taking Google + Dropbox + OneDrive factories; existing overloads forward `new OneDriveClientFactory()`); (b) add it to `TryBuildAdapterAsync`'s signature; (c) replace the `case "onedrive": …not yet supported…` arm (lines 260-265) with `TryBuildOneDriveAdapterAsync`; (d) add `TryBuildOneDriveAdapterAsync` mirroring `TryBuildDropboxAdapterAsync` (validate ClientID/EncryptedToken/EncryptedClientSecret, `TryDecrypt` both, parse optional `rootPath`, construct via `OneDriveSetup.CreateProviderFromConfigAsync`).
- `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` — update the `[InlineData("onedrive")]` "not yet supported" assertion (OneDrive is now supported) and add a OneDrive dispatch/construction test through a `FakeOneDriveClientFactory`.

Conditional (gated on Gate-1 answers — **not** touched unless approved):

- `tests/FlashSkink.Tests/Providers/GoogleDrive/GoogleDriveProviderTests.cs` and `tests/FlashSkink.Tests/Providers/Dropbox/DropboxProviderTests.cs` — add `using FlashSkink.Tests.Providers;` after the shared handler moves out of their sub-namespaces. **Conditional on Q5.**
- `Directory.Packages.props` — drop `Microsoft.Graph` (5.65.0) and `Microsoft.Identity.Client` (4.67.2) pins. **Conditional on Q2/Q8.**
- `src/FlashSkink.Core/Upload/UploadConstants.cs` — `RangeSize` 4 MiB → 5 MiB (5,242,880 = 16 × 320 KiB = 20 × 256 KiB), plus its blueprint-citing doc comment, plus the "4 MB" mentions in `BLUEPRINT.md` §15.5 / `dev-plan/phase-3-*` / `.claude/plans/pr-3.x` (doc drift only — `RangeUploaderTests` compute relative to the constant and survive). **Conditional on Q1 (Option E).**

## Files to delete

- `tests/FlashSkink.Tests/Providers/GoogleDrive/RecordingHttpMessageHandler.cs` (namespace `FlashSkink.Tests.Providers.GoogleDrive`). **Conditional on Q5.**
- `tests/FlashSkink.Tests/Providers/Dropbox/RecordingHttpMessageHandler.cs` (namespace `FlashSkink.Tests.Providers.Dropbox`). **Conditional on Q5.**

## Dependencies

- **No new NuGet packages.** Raw HTTP uses `System.Net.Http` + `System.Text.Json` (source-gen), already in the tree.
- If Q2/Q8 approved: **remove** `Microsoft.Graph` and `Microsoft.Identity.Client` `PackageVersion` entries.
- Project references: none added. `OneDrive` adapter lives in `FlashSkink.Core`, depends only on `FlashSkink.Core.Abstractions` + MEL abstractions (Principle 28).

## Public API surface

No new **public** types. The OneDrive adapter, setup, factory, bundle, config, and hash are all `internal` — consistent with §4.3/§4.4. The only contract touched is `BrainBackedProviderRegistry`'s **internal** `CreateAsync` overload chain, which gains an additive OneDrive-factory overload; the `public static CreateAsync(IBrainAccess, ReadOnlyMemory<byte>, ILoggerFactory, CancellationToken)` signature is unchanged.

The frozen `IStorageProvider` / `IProviderSetup` / `ISupportsRemoteHashCheck` / `UploadSession` / `ProviderHealth` contracts (Principle 23) are **not** modified — additive-only, and this PR adds nothing to them.

## Internal types

### `FlashSkink.Core.Providers.OneDrive.OneDriveProvider` (`internal sealed partial class : IStorageProvider, IAsyncDisposable`)
- `string ProviderID { get; }`, `string ProviderType => "onedrive"`, `string DisplayName { get; }`
- Holds: `OneDriveClientBundle _bundle`, `OneDriveProviderConfig _config` (root path), `ILogger<OneDriveProvider> _logger`, `int _disposed`, and an `_earlyFinalised` `ConcurrentDictionary<string, EarlyFinalisation>` keyed by `uploadUrl` (caches the DriveItem id + size when a range `PUT` returns 200/201 before `FinaliseUploadAsync` is called — mirrors Google's `_earlyFinalised`).
- `private const string GraphRoot = "https://graph.microsoft.com/v1.0"`, `private static readonly TimeSpan ResumableSessionTtl` (Graph sessions live ~hours; mirror Google's TTL field used in `BeginUploadAsync` session metadata).
- Implements all 12 `IStorageProvider` members + `DisposeAsync` (idempotent via `Interlocked.Exchange(ref _disposed, 1)`).
- Private `MapHttpFailureAsync<T>(HttpResponseMessage, string operation, CancellationToken)` and a non-generic delegating overload (mirror Google), mapping Graph status/error bodies onto existing `ErrorCode`s (table below).
- Private `OneDriveSessionUri` static helper: `Encode(string uploadUrl, string remoteName)` → JSON envelope `{"u":"…","n":"…"}`, base64; `TryDecode(string, out Decoded)` — **never throws**, every malformed/empty input → `false` (caller maps to `UploadSessionExpired`). Mirrors `DropboxSessionUri`.
- Private `ResponseOwningStream : Stream` wrapping the download body + owning the `HttpResponseMessage` (dispose on close); ownership transferred via `response = null` on the success path so the `finally`/catch dispose doesn't double-free (Principle 16). Mirror of Google's.

### `FlashSkink.Core.Providers.OneDrive.OneDriveSetup` (`internal sealed partial class : IProviderSetup`)
- `string ProviderType => "onedrive"`, `string DisplayName => "OneDrive"`, `ProviderSetupKind SetupKind => ProviderSetupKind.OAuthAuthorizationCode` (match Google/Dropbox value).
- Endpoint constants: `internal const string AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize"`, `internal const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token"`, `internal const string Scopes = "Files.ReadWrite offline_access"`.
- Three-constructor pattern (mirror Google/Dropbox): `public OneDriveSetup(ILoggerFactory)` → delegates with `new OneDriveClientFactory()`, `new HttpClient()`, `ownsTokenClient: true`; `internal OneDriveSetup(IOneDriveClientFactory, HttpClient, ILoggerFactory)` (test, `ownsTokenClient: false`); `private` full ctor.
- `GetAuthorizationUriAsync` builds the v2.0 authorize URL via `StringBuilder`: `client_id`, `response_type=code`, `redirect_uri`, `scope=Files.ReadWrite offline_access`, `code_challenge`, `code_challenge_method=S256`, `response_mode=query`. (`offline_access` is what yields a refresh token.)
- `ExchangeCodeAsync` POSTs `FormUrlEncodedContent` to `TokenEndpoint`: `client_id`, `scope`, `code`, `redirect_uri`, `grant_type=authorization_code`, `code_verifier`; `client_secret` **only if** `credentials.ClientSecret` is non-empty (Q4 — supports both "Web"/confidential and "native"/public app registrations). Parses via source-gen `TokenResponseShape` (`access_token`, `refresh_token`, `expires_in`); encrypts the **refresh token** via `ProviderTokenCrypto.Encrypt(refreshToken, dek)`. `TryParseErrorTag` reads only the `error` field — **never** `error_description` (Principle 26).
- `CreateProviderAsync` (interface) → validate + `TryDecrypt` + delegate to `internal CreateProviderFromConfigAsync(string providerId, string displayName, string clientId, string clientSecret, string refreshToken, string? rootPath, CancellationToken ct)` — the path used by the registry, which holds already-decrypted credentials.
- `ValidatePathAsync` — OneDrive path rules (no leading/trailing slash issues, illegal chars `\ / : * ? " < > |` in segment names, length limits); returns `ValidationResult`. Default root `ProviderConstants.CloudRootFolderName` ("FlashSkink Backup") when empty.

### `FlashSkink.Core.Providers.OneDrive.OneDriveClientFactory` (`internal sealed class : IOneDriveClientFactory`)
- `Result<OneDriveClientBundle> Create(string clientId, string clientSecret, string refreshToken, ILoggerFactory loggerFactory)`.
- Builds the authed `HttpClient` over a `MicrosoftAuthDelegatingHandler` (which owns refresh) and a plain `HttpClient` (no handler) for `uploadUrl` traffic; packs both into an `OneDriveClientBundle`.

### `FlashSkink.Core.Providers.OneDrive.MicrosoftAuthDelegatingHandler` (`internal sealed class : DelegatingHandler`)
- Holds `clientId`, `clientSecret` (may be empty), `refreshToken`, cached `accessToken` + `expiresAtUtc`, a refresh-gate `SemaphoreSlim`, `ILogger`.
- `SendAsync`: `ApplyAuthHeaderAsync` (refresh if expired/absent) → `base.SendAsync` → on **401**: dispose response, `RefreshTokenAsync` (form-POST `grant_type=refresh_token`, `client_id`, `client_secret` if present, `refresh_token`, `scope`), `CloneRequestAsync` (buffer content via `ReadAsByteArrayAsync`), retry **once**. Refresh failure → synthetic 401 `HttpResponseMessage` so the provider's catch maps to `TokenRefreshFailed` rather than surfacing a raw exception (mirror of Google's `TokenResponseException` → synthetic-401 trick). Token value never logged (Principle 26).

### `FlashSkink.Core.Providers.OneDrive.OneDriveClientBundle` (`internal sealed class : IDisposable`)
- `required HttpClient AuthedClient`, `required HttpClient PlainClient`; idempotent `Dispose` (both clients, guarded).

### `FlashSkink.Core.Providers.OneDrive.OneDriveProviderConfig` (`internal sealed record`)
- `[JsonPropertyName("rootPath")] required string RootPath` + `[JsonSerializable]` source-gen context with `JsonKnownNamingPolicy.CamelCase`.

### `FlashSkink.Core.Providers.OneDrive.OneDriveQuickXorHash` (`internal static class`)
- `const int WidthInBits = 160`, `const int BlockSize = …` (pooled read buffer).
- `static Task<string> ComputeAsync(Stream source, CancellationToken ct)` — streams input, QuickXorHash 160-bit shift-register XOR + 8-byte little-endian length XOR into the final bytes, returns base64. Empty input → base64 of the zero/​length-only state (known vector). Cancellation → `string.Empty`. Pooled buffer returned in `finally`; no `stackalloc` across `await` (Principle 20).

### `FlashSkink.Core.Providers.OneDrive.OneDriveProvider.TokenResponseShape` / `TokenErrorShape` (source-gen JSON records)
- `TokenResponseShape { access_token, refresh_token, expires_in }`; `TokenErrorShape { error }` (intentionally omits `error_description`, Principle 26). Same pattern as Google's.

## Method-body contracts

`OneDriveProvider` (all public methods return `Result`/`Result<T>`, never throw — Principle 1; `OperationCanceledException` first → `Cancelled`, then specific, then `Exception` → `Unknown` — Principles 14/15; every `ct` last and named `ct` — Principle 13):

- **`BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct)`** → `POST {GraphRoot}/me/drive/root:/{rootPath}/{remoteName}:/createUploadSession` (authed client) with body `{"item":{"@microsoft.graph.conflictBehavior":"replace","name":"{remoteName}"}}`. 200 → parse `uploadUrl`; return `UploadSession` whose `SessionUri = OneDriveSessionUri.Encode(uploadUrl, remoteName)`, `ProviderID`, `RemoteName`, `TotalBytes`, `ExpiresUtc` from `expirationDateTime`. Non-200 → `MapHttpFailureAsync`.
- **`UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)`** → `TryDecode` session (fail → `UploadSessionExpired`); `PUT {uploadUrl}` on the **plain** client with `Content-Length`, `Content-Range: bytes {offset}-{offset+len-1}/{session.TotalBytes}`, `Content-Type: application/octet-stream`, body `new ReadOnlyMemoryContent(data)`, **no `Authorization`**. 202 → `Result.Ok` (more expected). 200/201 → final DriveItem arrived early: cache `(id, size)` in `_earlyFinalised[uploadUrl]`, `Result.Ok`. 416 → range already received, `Result.Ok` (idempotent resume). 404 → session gone → `UploadSessionExpired`. 5xx → `UploadFailed`. 429 → `ProviderRateLimited`. **Contract note:** every non-final range handed by `RangeUploader` is exactly `UploadConstants.RangeSize`; correctness depends on that value being a 320 KiB multiple (Q1).
- **`GetUploadedBytesAsync(UploadSession session, CancellationToken ct)`** → if `_earlyFinalised` has it, return `session.TotalBytes`; else `GET {uploadUrl}` (plain client) → parse `nextExpectedRanges[0]` start offset → uploaded = that start. 404 → `UploadSessionExpired`. (Resume support, Principle 5.)
- **`FinaliseUploadAsync(UploadSession session, CancellationToken ct)`** → if `_earlyFinalised[uploadUrl]` present, return its id (log id+size+hash at `Information`); else the session never produced a final DriveItem → treat as not-yet-complete: re-`GET` status; if Graph reports complete return id, else `UploadFailed`. On success log `"OneDrive accepted file for {ProviderId}; id={Id}, size={Size}, quickXorHash={Hash}"` at `Information` (server-returned `file.hashes.quickXorHash` / `sha1Hash`, `"<absent>"` if none). Returns DriveItem `id` as `remoteId`. **No** local-hash comparison in V1 (Q3).
- **`AbortUploadAsync(UploadSession session, CancellationToken ct)`** → `DELETE {uploadUrl}` (plain client). 204/404 → `Result.Ok` (gone is success). Other → `MapHttpFailureAsync`. (Graph supports abort — unlike Dropbox.)
- **`DownloadAsync(string remoteId, CancellationToken ct)`** → `GET {GraphRoot}/me/drive/items/{remoteId}/content` (authed, `HttpCompletionOption.ResponseHeadersRead`). 200 → `ResponseOwningStream` over the body, transfer ownership. 404 → `BlobNotFound`. Else `MapHttpFailureAsync`.
- **`DeleteAsync(string remoteId, CancellationToken ct)`** → `DELETE {GraphRoot}/me/drive/items/{remoteId}`. 204/404 → `Result.Ok`. Else map.
- **`ExistsAsync(string remoteId, CancellationToken ct)`** → `GET {GraphRoot}/me/drive/items/{remoteId}?$select=id`. 200 → `Ok(true)`. 404 → `Ok(false)`. Else map.
- **`ListAsync(string prefix, CancellationToken ct)`** → `GET {GraphRoot}/me/drive/root:/{rootPath}/{prefix}:/children?$select=name` (or `:/children` at root), follow `@odata.nextLink` paging; return child names. 404 → empty list. Else map.
- **`CheckHealthAsync(CancellationToken ct)`** → `GET {GraphRoot}/me/drive?$select=id` → `ProviderHealth` (reachable + last-checked). Auth failure → unhealthy with `TokenRefreshFailed` context. Map transport errors to unhealthy, not thrown.
- **`GetUsedBytesAsync` / `GetQuotaBytesAsync`** → `GET {GraphRoot}/me/drive?$select=quota` → `quota.used` / `quota.total` (`GetQuotaBytesAsync` returns `Result<long?>`, `null` when Graph omits `total`, e.g. unlimited).

**Failure-mapping table (`MapHttpFailureAsync` — every branch onto an existing `ErrorCode`, mirrors Google):**

| Graph response | `ErrorCode` | RangeUploader bucket |
|---|---|---|
| 401 (after handler's refresh-retry) | `TokenRefreshFailed` | fail |
| 403 + `quotaLimitReached` / 507 Insufficient Storage | `ProviderQuotaExceeded` | fail |
| 403 + `activityLimitReached` / 429 (+ `Retry-After`) | `ProviderRateLimited` | retryable |
| 403 (other) | `ProviderAuthFailed` | fail |
| 404 on `uploadUrl` (session) | `UploadSessionExpired` | restart-from-0 |
| 404 on item (download/exists/delete) | `BlobNotFound` / `Ok(false)` / `Ok` | — |
| 416 on range PUT | `Ok` (already received) | — (continue) |
| 5xx | `UploadFailed` (body excerpt in `Metadata`) | retryable |
| transport (`HttpRequestException`/timeout) | `ProviderUnreachable` | retryable |
| anything else | `Unknown` | fail |

`MicrosoftAuthDelegatingHandler.RefreshTokenAsync`: success → cache token+expiry; HTTP/parse failure → synthetic 401 response (so the provider maps to `TokenRefreshFailed`). `OperationCanceledException` propagates (handler is below the provider's catch).

`OneDriveSessionUri.TryDecode` robustness contract: returns `false` (never throws) for null/empty/non-base64/malformed-JSON/missing-field input; the provider maps `false` → `UploadSessionExpired`. (Mirror of `DropboxSessionUri` — verified by a dedicated robustness test.)

## Integration points

- **`IStorageProvider`** (frozen, §10.5) — 12 members listed above; `data` per range is "up to `UploadConstants.RangeSize`".
- **`IProviderSetup`** (frozen) — `GetAuthorizationUriAsync`, `ExchangeCodeAsync`, `ValidatePathAsync`, `CreateProviderAsync` signatures consumed verbatim.
- **`ProviderTokenCrypto.Encrypt` / `TryDecrypt`** (`FlashSkink.Core.Providers`) — refresh-token + client-secret envelope `[0x01][12B nonce][ct][16B tag]`, AAD empty; `TryDecrypt` never throws.
- **`ProviderConstants.CloudRootFolderName`** ("FlashSkink Backup") — default root path.
- **`UploadSession`, `ProviderHealth`, `ValidationResult`, `ProviderCredentials`, `ProviderSetupKind`** — abstraction DTOs consumed as-is.
- **`BrainBackedProviderRegistry`** — `TryBuildOneDriveAdapterAsync` mirrors `TryBuildDropboxAdapterAsync` (lines 415-495): validate ClientID/EncryptedToken/EncryptedClientSecret → `TryDecrypt` appSecret + refreshToken → parse optional `rootPath` from `OneDriveProviderConfig` → `using var oauthClient = new HttpClient(); new OneDriveSetup(oneDriveFactory, oauthClient, loggerFactory).CreateProviderFromConfigAsync(...)`. `ProviderRow` record unchanged (already carries all needed columns).
- **`RangeUploader`** (NOT modified — Decision 6) — drives `BeginUploadAsync`/`UploadRangeAsync`/`GetUploadedBytesAsync`/`FinaliseUploadAsync`/`AbortUploadAsync` and buckets results by `ErrorCode` (mapping table above is built to land in the right bucket).

## Principles touched

- **1** — Core never throws across the public API; every adapter/setup method returns `Result`/`Result<T>`.
- **12** — OS-agnostic; raw HTTP + `System.Text.Json`, no platform APIs or paths.
- **13** — every async method takes `CancellationToken ct` last.
- **14** — `OperationCanceledException` is the first catch on every cancellable method → `Cancelled`, logged Info/Debug.
- **15** — no bare `catch (Exception)` alone; `HttpRequestException` (and status-filtered branches) before the final `Exception` → `Unknown`.
- **16** — partial-resource disposal: `ResponseOwningStream` ownership transfer (`response = null`), `using` token-exchange/oauth clients, idempotent bundle/provider dispose.
- **18** — does **not** apply (OAuth/setup/non-hot path); explicitly *not* pooling in setup, per the principle's own carve-out.
- **23** — provider contract frozen; OneDrive adds a new implementation, modifies no signatures; QuickXorHash is shipped without touching `ISupportsRemoteHashCheck`.
- **24** — no silent background failures: the provider returns `Result`; the bus-publishing owner is `UploadQueueService`, unchanged.
- **25** — appliance vocabulary: no "OAuth"/"DEK"/"blob"/"nonce"/"upload session" in any user-visible string; user-facing copy says skink/tail/file/folder/recovery phrase. (Internal log lines may use technical terms — they are not user-visible.)
- **26** — no secrets in logs: access/refresh tokens, client secret, `error_description` never logged; `ErrorContext.Metadata` keys never match `*Token`/`*Key`/`*Password`/`*Secret`/`*Mnemonic`/`*Phrase`.
- **28** — Core references only MEL abstractions; Serilog stays in CLI.
- **31** — refresh tokens / client secrets held as cleartext strings only transiently (unavoidable .NET string constraint — same as Google/Dropbox); the handler caches the access token in-process only, never to disk.
- **32** — no telemetry/update checks; only user-initiated Graph calls.
- **37** — encode invariants via flow attributes; the one sanctioned `!` is the generic-helper `typed.Error!` (carries the one-line invariant comment), mirroring Google's `MapHttpFailureAsync`.

## Test spec

### `tests/FlashSkink.Tests/Providers/OneDrive/OneDriveProviderTests.cs` (class `OneDriveProviderTests`)
- `BeginUploadAsync_Success_ReturnsSessionWithEncodedUri` — 200 createUploadSession → `UploadSession.SessionUri` round-trips via `OneDriveSessionUri.TryDecode`; asserts the `PUT` was not yet made.
- `BeginUploadAsync_PathAddressing_PostsToRootColonPath` — asserts the request URL is `…/root:/FlashSkink Backup/{name}:/createUploadSession` (path-addressed, no folder lookup).
- `UploadRangeAsync_202_ReturnsOkMoreExpected`.
- `UploadRangeAsync_OmitsAuthorizationHeaderOnPlainClient` — asserts the recorded `PUT` to `uploadUrl` carries `Content-Range` but **no** `Authorization`.
- `UploadRangeAsync_200_CachesEarlyFinalisationAndFinaliseReturnsId`.
- `UploadRangeAsync_416_TreatedAsAlreadyReceived_ReturnsOk`.
- `UploadRangeAsync_404_MapsUploadSessionExpired`.
- `UploadRangeAsync_MalformedSessionUri_MapsUploadSessionExpired` (TryDecode robustness).
- `GetUploadedBytesAsync_ParsesNextExpectedRanges`.
- `FinaliseUploadAsync_LogsServerHashAndSize_NoLocalCompare` — asserts `Information` log contains id/size/quickXorHash and that **no** download/local-hash call occurred (Q3).
- `AbortUploadAsync_204_ReturnsOk` / `AbortUploadAsync_404_ReturnsOk`.
- `DownloadAsync_200_StreamsBody_DisposesResponseOnClose`.
- `DownloadAsync_404_MapsBlobNotFound`.
- `DeleteAsync_404_ReturnsOk`.
- `ExistsAsync_200_True` / `ExistsAsync_404_False`.
- `ListAsync_FollowsNextLink_AggregatesNames`.
- `CheckHealthAsync_Reachable_ReturnsHealthy` / `CheckHealthAsync_401_ReturnsUnhealthyTokenRefreshFailed`.
- `GetQuotaBytesAsync_TotalAbsent_ReturnsNull`.
- `MapHttpFailure_403QuotaLimitReached_MapsProviderQuotaExceeded` / `_429_MapsProviderRateLimited` / `_5xx_MapsUploadFailed` (`[Theory]` over the mapping table).
- `Auth401ThenRefresh_RetriesOnceWithNewToken` — handler-level: first authed call 401, refresh 200, retry 200; asserts exactly one refresh + token never appears in any recorded header log.
- `RefreshFailure_MapsTokenRefreshFailed` (synthetic-401 path).
- `DisposeAsync_Idempotent_SafeToCallTwice`.

### `tests/FlashSkink.Tests/Providers/OneDrive/OneDriveSetupTests.cs` (class `OneDriveSetupTests`)
- `GetAuthorizationUriAsync_BuildsV2UrlWithPkceAndOfflineAccess` — asserts host `login.microsoftonline.com/common/oauth2/v2.0/authorize`, `scope=Files.ReadWrite offline_access`, `code_challenge_method=S256`, `response_type=code`.
- `ExchangeCodeAsync_Success_EncryptsRefreshToken` — POST parsed; returns `ProviderTokenCrypto.Encrypt`-shaped envelope that `TryDecrypt` recovers to the original refresh token.
- `ExchangeCodeAsync_WithClientSecret_IncludesSecretInForm` / `ExchangeCodeAsync_PublicApp_OmitsClientSecret` (Q4).
- `ExchangeCodeAsync_ErrorResponse_MapsWithoutLeakingErrorDescription` — error body has `error` + `error_description`; result message contains neither token nor `error_description` (Principle 26).
- `ValidatePathAsync_RejectsIllegalSegmentChars` (`[Theory]` over `: * ? " < > |`).
- `ValidatePathAsync_EmptyPath_DefaultsToBackupRoot`.
- `CreateProviderAsync_DecryptsAndBuildsProvider` (through `FakeOneDriveClientFactory`).
- `CreateProviderAsync_BadEnvelope_FailsCleanly` (TryDecrypt false → failed Result, no throw).

### `tests/FlashSkink.Tests/Providers/OneDrive/OneDriveQuickXorHashTests.cs` (class `OneDriveQuickXorHashTests`)
- `ComputeAsync_KnownVector_MatchesExpectedBase64` (`[Theory]` — Microsoft-documented QuickXorHash test vectors authored inline).
- `ComputeAsync_EmptyInput_ReturnsEmptyStateHash`.
- `ComputeAsync_StreamedInChunks_EqualsSingleShot` (streaming equivalence across `BlockSize` boundary).
- `ComputeAsync_Cancelled_ReturnsEmptyString`.

### `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` (modify)
- Update `[InlineData("onedrive")]` "not yet supported" case → OneDrive now constructs.
- Add `OneDriveRow_BuildsAdapter_ThroughFakeFactory` — a `Providers` row with `ProviderType="onedrive"`, valid encrypted token+secret, `{"rootPath":"/FlashSkink Backup"}` config → registry yields a live adapter via `FakeOneDriveClientFactory`.

## Acceptance criteria
- [ ] Builds with zero warnings on `ubuntu-latest` + `windows-latest` (`--warnaserror`).
- [ ] All new tests pass; no existing tests break.
- [ ] `BrainBackedProviderRegistry` constructs a OneDrive adapter; the "not yet supported" stub is gone.
- [ ] `OneDriveProvider` implements all 12 `IStorageProvider` members; `IStorageProvider`/`IProviderSetup` signatures unchanged (Principle 23).
- [ ] No new `ErrorCode` values (phase-4 acceptance).
- [ ] Range `PUT` to `uploadUrl` carries no `Authorization` header (asserted by test).
- [ ] No secrets in any log line or `ErrorContext.Metadata` (Principle 26); CI secret-lint passes.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] `assembly-layering` test passes (no UI/Graph-SDK leakage if Q2 approved).

## Line-of-code budget
- `OneDriveProvider.cs` — ~950
- `OneDriveSetup.cs` — ~490
- `OneDriveClientFactory.cs` (+ handler) — ~230
- `OneDriveClientBundle.cs` — ~60
- `IOneDriveClientFactory.cs` — ~45
- `OneDriveProviderConfig.cs` — ~28
- `OneDriveQuickXorHash.cs` — ~90
- `BrainBackedProviderRegistry.cs` (delta) — ~+70
- Production total: ~1,900 non-test
- `OneDriveProviderTests.cs` — ~720
- `OneDriveSetupTests.cs` — ~410
- `OneDriveQuickXorHashTests.cs` — ~120
- `FakeOneDriveClientFactory.cs` — ~90
- `OneDriveCannedResponses.cs` — ~160
- `RecordingHttpMessageHandler.cs` (shared, mostly moved) — ~160
- `BrainBackedProviderRegistryTests.cs` (delta) — ~+50
- Test total: ~1,700

## Non-goals
- Do **NOT** implement `ISupportsRemoteHashCheck` on `OneDriveProvider` (matches §4.3/§4.4 — QuickXorHash ships unwired for Phase 5).
- Do **NOT** modify `RangeUploader`, `UploadQueueService`, or `BrainMirrorService` (Decision 6). (The `UploadConstants.RangeSize` value is the *only* adjacent item, and only if Q1 → Option E.)
- Do **NOT** add any `ErrorCode` value.
- Do **NOT** wire OneDrive into the CLI `tail add` command — that is §4.6 (provider-setup CLI).
- Do **NOT** implement delta-sync, sharing links, or large-file conflict policies beyond `replace`.
- Do **NOT** add the Graph SDK or MSAL packages (raw-HTTP path); if Q2 declined, the plan is rewritten, not extended.
- Do **NOT** persist `rootPath` from setup — that write is §4.6's `AddTailAsync`; this PR only *reads* it on open.

## Open questions for Gate 1 review

1. **[BLOCKING] 320 KiB fragment alignment.** Graph requires each non-final `PUT` fragment to be a multiple of 320 KiB; `UploadConstants.RangeSize = 4 MiB` is not, so any blob > 4 MiB breaks OneDrive's commit.
   - **Option E (recommended):** change `RangeSize` 4 MiB → **5 MiB** (5,242,880 = 16 × 320 KiB = 20 × 256 KiB; satisfies OneDrive *and* Google, no effect on Dropbox/FileSystem; lands in Graph's recommended 5–10 MiB window). One-line constant change; `RangeUploaderTests` compute relative to it and survive; but it touches a blueprint-cited decision constant (§15.5 B3-b) and the "4 MB" doc mentions in `BLUEPRINT.md`/`dev-plan`/`pr-3.x` need a drift pass. Brushes (does not violate) Decision 6's spirit — `UploadConstants` is not on its do-not-touch list.
   - **Option F:** keep 4 MiB; add a OneDrive-provider-internal re-chunking buffer that accumulates to 320 KiB-aligned amounts and holds the remainder across `UploadRangeAsync` calls. Keeps the constant but introduces cross-call in-memory upload state — brushes Principle 5 (session state on the skink, not memory) and Principle 18, ~100+ extra lines, fragile on resume.
   - **My recommendation: Option E.** It's the smaller, lower-risk change and aligns all three cloud providers on one range size. Need your call because it edits a blueprint-cited constant.

2. **SDK vs raw HTTP.** Dev-plan says "Microsoft.Graph wiring." I recommend **raw HTTP** + `MicrosoftAuthDelegatingHandler` (MSAL-free), because the resumable `PUT` must omit `Authorization` (SDK/MSAL fight this) and the §4.3 Google adapter already proved the raw-HTTP pattern. Choosing the SDK is a material rewrite of `OneDriveProvider`/`OneDriveClientFactory`. **Confirm raw HTTP?**

3. **QuickXorHash / `ISupportsRemoteHashCheck`.** Following the §4.4 `DropboxContentHash` precedent: implement + unit-test `OneDriveQuickXorHash`, but do **not** wire it or implement `ISupportsRemoteHashCheck`; `FinaliseUploadAsync` logs the server-returned hash + size at `Information`. (This corrects an earlier note that suggested skipping the algorithm entirely.) **Confirm.**

4. **`client_secret` optionality.** PKCE always; include `client_secret` in the token POST only when `credentials.ClientSecret` is non-empty — supporting both confidential ("Web") and public ("native") Azure app registrations. **Confirm.**

5. **`RecordingHttpMessageHandler` extraction.** §4.4's plan (Discrepancy 2) deferred this to §4.5. Recommend extracting the generic recorder to `tests/FlashSkink.Tests/Providers/RecordingHttpMessageHandler.cs` (namespace `FlashSkink.Tests.Providers`), deleting the two sub-namespace copies, and adding `using FlashSkink.Tests.Providers;` to `GoogleDriveProviderTests.cs` + `DropboxProviderTests.cs`. **Approve touching those two existing test files + the two deletes?** (If declined, OneDrive tests get a third copy.)

6. **Registry wiring shape.** Add `IOneDriveClientFactory` additively to the `CreateAsync` overload chain (new most-internal overload; existing overloads forward `new OneDriveClientFactory()`), thread it into `TryBuildAdapterAsync`, replace the "onedrive → not yet supported" arm with `TryBuildOneDriveAdapterAsync`, and update the registry test. **Confirm this is the expected wiring.**

7. **Update the `[InlineData("onedrive")]` "not yet supported" registry test.** It currently asserts the stub behaviour; it must flip to asserting construction. **Confirm.** (Bundled with Q6.)

8. **Drop `Microsoft.Graph` + `Microsoft.Identity.Client` pins.** If Q2 → raw HTTP, these two `Directory.Packages.props` entries become unused. Recommend removing them in this PR (keeps the dependency surface honest; `assembly-layering` stays green). **Approve editing `Directory.Packages.props`?**
