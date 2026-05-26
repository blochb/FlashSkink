# PR 4.3 — Google Drive provider and setup

**Branch:** `pr/4.3-google-drive-provider`
**Blueprint sections:** §10.1–10.4 (provider contract), §13.6 (blob format — context for what gets uploaded), §15.2 (Drive resumable-upload protocol mapping), §15.3 (session lifecycle), §15.7 (Drive verification — `md5Checksum`), §24.3 (Drive setup walkthrough), §24.5 (loopback OAuth — consumed via `IOAuthCaptureFlow`), §27.2 (Drive provider notes).
**Dev plan section:** phase-4-providers §4.3.
**Phase-4 cross-cutting decisions touched:** 1 (frozen contract — consume, do not edit), 2 (token crypto via `ProviderTokenCrypto`), 3 (OAuth via `IOAuthCaptureFlow`), 4 (HTTP client per-volume — `DriveService` constructed in setup, cached by registry), 5 (`ProviderConstants.CloudRootFolderName` is `"FlashSkink Backup"`), 6 (provider-specific verification — see Discrepancies below).

---

## Scope

Ship Google Drive as the first real cloud `IStorageProvider`. Five production files plus tests:

1. **`GoogleDriveProvider`** (internal sealed, in `Core/Providers/GoogleDrive/`) — the `IStorageProvider` adapter. Constructed by `GoogleDriveSetup.CreateProviderAsync` from a `DriveService` (Google SDK) plus a raw `HttpClient` for the resumable-upload protocol (Drive's resumable PUTs bypass the SDK's `MediaUpload` helper because that helper runs the whole upload as one operation rather than handing single ranges back to `RangeUploader`).
2. **`GoogleDriveSetup`** (internal sealed, in `Core/Providers/GoogleDrive/`) — the `IProviderSetup` adapter. Builds the authorisation URL, exchanges the code at `https://oauth2.googleapis.com/token`, decrypts the persisted refresh token at registry-construction time, and constructs `GoogleDriveProvider`.
3. **`GoogleDriveClientFactory`** (internal sealed, in `Core/Providers/GoogleDrive/`) — single seam responsible for turning a `(clientId, clientSecret, refreshToken)` triple into a ready-to-use `DriveService` + companion `HttpClient`. Tests substitute a fake that returns a `DriveService` backed by a recording `HttpMessageHandler`.
4. **`GoogleDriveProviderConfig`** (internal sealed record, in `Core/Providers/GoogleDrive/`) — JSON-serialisable shape persisted into `Providers.ProviderConfig` carrying the `FolderId` (the `"FlashSkink Backup"` folder ID Drive returned at setup time). Source-generated `JsonSerializerContext` for AOT-friendly serialisation, matching the existing `FileSystemProviderConfig` pattern.
5. **`BrainBackedProviderRegistry`** (modify) — fill the `"google-drive"` arm in `TryBuildAdapter` so registry construction at volume open resolves Drive rows to live `GoogleDriveProvider` instances. The arm calls `GoogleDriveSetup.CreateProviderAsync` with the row's `(ProviderID, DisplayName, EncryptedToken, ProviderConfig)`.

Plus the supporting integration: `Google.Apis.Drive.v3` lands as a package reference on `FlashSkink.Core`, brain reads must surface `Providers.EncryptedToken`, `Providers.EncryptedClientSecret`, and `Providers.ProviderConfig` (the §4.1 SELECT only reads `ProviderConfig`, so we extend it).

### Out of scope (deferred to later PRs)

- The actual `setup add` / `setup remove` / `setup list` CLI commands — those are §4.6. §4.3 produces only the `IProviderSetup` implementation; the CLI wiring lands in §4.6 together with public `AddTailAsync`.
- Witness writes (`_witness/current.enc`) to Drive — §3.5.2's witness contract holds across all providers, but exercising it against Drive is a §4.6 end-to-end test concern. §4.3 ships the upload primitives that make this work; the witness path itself is just one more remote name routed through the same provider surface.
- Brain-mirror writes (`_brain/{timestamp}.bin`) to Drive — same as witness, routed through the same `BeginUploadAsync` / `UploadRangeAsync` path.
- Cross-tail post-finalisation verification using Drive's `md5Checksum` — see Discrepancy 1 below. The current `ISupportsRemoteHashCheck` interface is XXHash64-only and cannot express MD5. Cross-tail verification with provider-specific algorithms is a Phase 5 (healing / cross-verification) concern. §4.3 commits to the GCM tag as the sole authenticator for Drive blobs (Principle 6 — every uploaded blob is AES-GCM authenticated; tampering would surface at download-time decryption).

---

## Discrepancies surfaced against the dev-plan text

Four drifts. Each is recorded for the audit trail; the Gate 1 review should confirm the resolution before implementation begins.

### Discrepancy 1 — Drive verification cannot use `ISupportsRemoteHashCheck`

**Dev-plan claim** (§4.3 key constraints): *"The MD5 hash check in §15.7 is implemented as `ISupportsRemoteHashCheck.GetRemoteHashAsync`. The local-blob MD5 is computed lazily inside `RangeUploader` (which already dispatches on the capability interface — no changes to `RangeUploader` are required)."*

**Reality:** `ISupportsRemoteHashCheck.GetRemoteXxHash64Async` returns `Result<ulong>` and `RangeUploader.FinaliseAndVerifyAsync` compares the returned value to `ParseEncryptedXxHash(blob.EncryptedXxHash)` — both XXHash64. Drive's metadata field is `md5Checksum` (128-bit MD5), structurally and algorithmically distinct from XXHash64. The two cannot be reconciled inside the existing single-capability interface, and `RangeUploader` does not currently compute MD5 of the local blob. Cross-cutting decision 6's claim "no changes to `RangeUploader` are required" is incorrect for any cloud provider whose remote hash is not XXHash64.

**Three resolution options considered:**

- **A. Skip remote-hash verification for Drive.** `GoogleDriveProvider` does NOT implement `ISupportsRemoteHashCheck`. `RangeUploader.FinaliseAndVerifyAsync` already takes the "trust the GCM tag" fall-through path when the capability is absent. The GCM authentication tag inside every uploaded blob is a strong cryptographic authenticator — tampering with the encrypted bytes would cause the next download's decryption to fail. Drive's `md5Checksum` is logged at `Information` level after finalisation for observability but is not used for the integrity decision.
- **B. Add a new capability interface** (e.g. `ISupportsRemoteMd5Check`) and modify `RangeUploader` to dispatch on it as well as `ISupportsRemoteHashCheck`. Computes the local-blob MD5 inside `RangeUploader` (already has `blob.BlobPath` and the absolute path). Honours the §15.7 intent literally.
- **C. Make Drive download the blob and compute XXHash64.** Implements `ISupportsRemoteHashCheck` by re-downloading every uploaded blob from Drive and re-hashing. Doubles egress per upload. Functionally correct but wasteful.

**Selected: Option A.** Rationale:
- The "no changes to `RangeUploader`" cross-cutting decision is explicit. Honouring it precludes B.
- C doubles egress for every upload — for a 10 GB file this is a 10 GB re-download per upload to satisfy a check that the GCM tag already provides.
- The GCM tag is cryptographically stronger than MD5 (which has known collision attacks). A successful download with a valid GCM tag is a stronger integrity statement than a successful MD5 match.
- Phase 5 (recovery, healing, cross-tail verification) is the natural home for per-provider verification with the algorithm the provider exposes. By Phase 5 we have all three cloud providers and can design the capability interface across all three at once rather than one-at-a-time. Each provider in Phase 4 ships *without* cross-tail verification beyond the GCM tag; Phase 5 adds the capability interface and `RangeUploader` dispatch in a single coherent change.
- Drive's `md5Checksum` is still surfaced — `GoogleDriveProvider` logs it at `Information` after `FinaliseUploadAsync` ("Drive accepted blob {RemoteId} ({Bytes} bytes); md5Checksum={Md5}"). The data is captured for any future Phase 5 work without forcing a contract change here.

**Phase-impact:** the acceptance criterion *"Witness writes (§3.5.2) are exercised against the new cloud adapters in unit tests (against the SDK fakes) — the witness contract holds across all four providers"* is preserved. The witness contract is the GCM-authenticated blob containing the volume's identity tuple — it does not depend on `ISupportsRemoteHashCheck`. The criterion *"No new `ErrorCode` values added"* is preserved.

### Discrepancy 2 — `GoogleDriveProvider` visibility

The dev plan and §4.1/§4.2 precedents do not pin visibility. The §4.1 `BrainBackedProviderRegistry` is `public sealed`; `FileSystemProviderSetup` is `internal sealed`. `GoogleDriveProvider` and `GoogleDriveSetup` are both `internal sealed` — no public consumer needs to construct them directly. The CLI (§4.6) will not name them; it will name `IProviderSetup` and route through `BrainBackedProviderRegistry`. Tests use `[InternalsVisibleTo("FlashSkink.Tests")]` already.

### Discrepancy 3 — Refresh-token decryption needs the encrypted client secret too

The dev plan describes `GoogleDriveSetup.CreateProviderAsync` as "decrypts the refresh token, constructs a `UserCredential`". The Drive token-exchange endpoint also requires the **client secret**. Cross-cutting decision 2 commits to encrypting both `EncryptedToken` and `EncryptedClientSecret` via `ProviderTokenCrypto`. `CreateProviderAsync`'s signature already takes a `ProviderCredentials` parameter — the convention from §4.1 / §4.2 is that `credentials.ClientSecret` is **already decrypted** by the caller before `CreateProviderAsync` runs.

Two callers:
- **`SetupAddCommand`** (§4.6) — has cleartext credentials from the CLI args; passes them directly into `CreateProviderAsync`.
- **`BrainBackedProviderRegistry.CreateAsync`** — reads the encrypted form from `Providers.EncryptedClientSecret`, decrypts via `ProviderTokenCrypto.TryDecrypt`, and constructs a `ProviderCredentials` with the cleartext before calling `CreateProviderAsync`.

The brain-side decryption lives in `BrainBackedProviderRegistry` (the only registry-level holder of the DEK at construction time) rather than inside `GoogleDriveSetup`. The setup contract is intentionally agnostic about persistence — keeping it free of brain-row knowledge.

### Discrepancy 4 — `BrainBackedProviderRegistry` brain SELECT must read three extra columns

The §4.1 implementation reads only `ProviderID, ProviderType, DisplayName, ProviderConfig`. §4.3 needs `EncryptedToken` and `EncryptedClientSecret` too, plus optionally `ClientID` (the non-secret OAuth client ID, persisted to `Providers.ClientID` per the schema). The SELECT is extended and the `ProviderRow` record gains the matching fields. The FileSystem dispatch arm continues to ignore them.

---

## Files to create

- `src/FlashSkink.Core/Providers/GoogleDrive/GoogleDriveProvider.cs` — the `IStorageProvider` + `IAsyncDisposable` adapter. ~520 lines.
- `src/FlashSkink.Core/Providers/GoogleDrive/GoogleDriveSetup.cs` — the `IProviderSetup` adapter. ~340 lines.
- `src/FlashSkink.Core/Providers/GoogleDrive/IGoogleDriveClientFactory.cs` — internal interface for the SDK-construction seam. ~30 lines.
- `src/FlashSkink.Core/Providers/GoogleDrive/GoogleDriveClientFactory.cs` — production implementation (calls Google SDK). ~140 lines.
- `src/FlashSkink.Core/Providers/GoogleDrive/GoogleDriveProviderConfig.cs` — JSON-serialisable record + source-generated context. ~50 lines.
- `tests/FlashSkink.Tests/Providers/GoogleDrive/GoogleDriveProviderTests.cs` — unit tests for the adapter against an injected `HttpMessageHandler` recording fake. ~620 lines.
- `tests/FlashSkink.Tests/Providers/GoogleDrive/GoogleDriveSetupTests.cs` — unit tests for the setup adapter. Tests the URL building, token exchange (against a fake handler), and `CreateProviderAsync` happy/sad paths. ~360 lines.
- `tests/FlashSkink.Tests/Providers/GoogleDrive/FakeGoogleDriveClientFactory.cs` — test seam returning a `DriveService` backed by a `RecordingHttpMessageHandler`. ~110 lines.
- `tests/FlashSkink.Tests/Providers/GoogleDrive/RecordingHttpMessageHandler.cs` — generic test double that returns scripted responses based on URL/method matching. ~140 lines.

## Files to modify

- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `<PackageReference Include="Google.Apis.Drive.v3" />`. Version comes from `Directory.Packages.props` (already pinned at `1.68.0.3656`).
- `src/FlashSkink.Core/Providers/BrainBackedProviderRegistry.cs` — extend `ProviderRow` to carry `ClientID`, `EncryptedToken`, `EncryptedClientSecret`. Extend the SELECT. Fill the `"google-drive"` switch arm in `TryBuildAdapter` (currently logs a warning and skips) with `TryBuildGoogleDriveAdapter`. Add `IGoogleDriveClientFactory` constructor parameter on the internal constructor (default production value supplied by the public `CreateAsync` static; tests supply a fake via a new `internal` overload). ~120 lines of delta.

---

## Dependencies

- **NuGet:** `Google.Apis.Drive.v3` (already pinned at `1.68.0.3656` in `Directory.Packages.props`, currently unreferenced). The package transitively pulls `Google.Apis`, `Google.Apis.Core`, `Google.Apis.Auth`, all on `netstandard2.0`/`netstandard2.1` and compatible with `net10.0`.
- **Project references:** none new.
- **BCL:** `System.Net.Http` (already available), `System.Text.Json` (already used), `System.Security.Cryptography` (already used for ProviderTokenCrypto).

---

## Public API surface

§4.3 adds no public types. All new types are `internal sealed`:

- `GoogleDriveProvider` — implements the existing public `IStorageProvider` contract.
- `GoogleDriveSetup` — implements the existing public `IProviderSetup` contract.
- `GoogleDriveClientFactory` / `IGoogleDriveClientFactory` — internal seam.
- `GoogleDriveProviderConfig` — internal record.

The §4.1 `BrainBackedProviderRegistry` already exposes `public static CreateAsync`; this PR adds an overloaded `internal static CreateAsync` that accepts an `IGoogleDriveClientFactory` (for tests). Production callers use the existing public signature, which internally constructs `new GoogleDriveClientFactory()` and forwards.

---

## Internal types

### `FlashSkink.Core.Providers.GoogleDrive.GoogleDriveProviderConfig` (internal sealed record)

```csharp
/// <summary>
/// Persisted JSON shape for <c>Providers.ProviderConfig</c> on Google Drive rows. Carries the
/// stable Drive folder ID for "FlashSkink Backup" — established once at setup time and reused on
/// every volume open to avoid a per-open lookup round-trip.
/// </summary>
internal sealed record GoogleDriveProviderConfig
{
    /// <summary>Drive file ID of the root <c>"FlashSkink Backup"</c> folder (stable; never null).</summary>
    [JsonPropertyName("folderId")]
    public required string FolderId { get; init; }

    /// <summary>The literal folder name used at setup; defaults to <see cref="ProviderConstants.CloudRootFolderName"/>.</summary>
    [JsonPropertyName("folderName")]
    public string FolderName { get; init; } = ProviderConstants.CloudRootFolderName;
}

[JsonSerializable(typeof(GoogleDriveProviderConfig))]
internal sealed partial class GoogleDriveProviderConfigJsonContext : JsonSerializerContext { }
```

### `FlashSkink.Core.Providers.GoogleDrive.IGoogleDriveClientFactory` (internal interface)

```csharp
internal interface IGoogleDriveClientFactory
{
    /// <summary>
    /// Constructs a <see cref="DriveService"/> + companion <see cref="HttpClient"/> bound to the
    /// supplied credentials. The <see cref="HttpClient"/> is used for the resumable-upload PUT
    /// protocol (raw PUTs to a session URI, not routed through the SDK's MediaUpload helper).
    /// The returned <see cref="GoogleDriveClientBundle"/> owns both — disposing it disposes both.
    /// </summary>
    /// <param name="clientId">OAuth client ID (BYOC).</param>
    /// <param name="clientSecret">OAuth client secret (BYOC; cleartext).</param>
    /// <param name="refreshToken">OAuth refresh token (cleartext, freshly decrypted from
    /// <c>ProviderTokenCrypto</c>).</param>
    /// <param name="loggerFactory">Logger factory for the bundle's internal logging.</param>
    Result<GoogleDriveClientBundle> Create(
        string clientId,
        string clientSecret,
        string refreshToken,
        ILoggerFactory loggerFactory);
}

internal sealed class GoogleDriveClientBundle : IDisposable
{
    public required DriveService DriveService { get; init; }

    /// <summary>HTTP client whose <see cref="HttpClient.DefaultRequestHeaders"/> include the
    /// <c>Authorization: Bearer {access_token}</c>; refresh is handled by a delegating handler that
    /// asks <see cref="DriveService.HttpClient.MessageHandler.Credential"/> for a fresh token on
    /// 401. This is the client used for raw range PUTs to the session URI returned by
    /// <c>files.insert?uploadType=resumable</c>.</summary>
    public required HttpClient ResumableUploadClient { get; init; }

    public void Dispose()
    {
        DriveService.Dispose();
        ResumableUploadClient.Dispose();
    }
}
```

### `FlashSkink.Core.Providers.GoogleDrive.GoogleDriveClientFactory` (internal sealed) — full body sketch

```csharp
internal sealed class GoogleDriveClientFactory : IGoogleDriveClientFactory
{
    private const string ApplicationName = "FlashSkink";

    public Result<GoogleDriveClientBundle> Create(
        string clientId, string clientSecret, string refreshToken, ILoggerFactory loggerFactory)
    {
        try
        {
            // 1. Build a TokenResponse with just the refresh token. The SDK will refresh access
            //    tokens lazily.
            var tokenResponse = new TokenResponse
            {
                RefreshToken = refreshToken,
                // Access token starts null; SDK refreshes on first call.
            };

            // 2. Build the GoogleAuthorizationCodeFlow with the BYOC client secrets.
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                },
                Scopes = [DriveService.ScopeConstants.DriveFile],
            });

            // 3. Build a UserCredential — wraps refresh logic.
            var userCred = new UserCredential(flow, /*userId*/ "user", tokenResponse);

            // 4. Construct the DriveService.
            var driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = userCred,
                ApplicationName = ApplicationName,
            });

            // 5. Build the companion HttpClient with a custom delegating handler that asks
            //    userCred for a fresh access token on each request.
            var resumableHandler = new GoogleAuthDelegatingHandler(userCred)
            {
                InnerHandler = new HttpClientHandler(),
            };
            var resumableClient = new HttpClient(resumableHandler);

            return Result<GoogleDriveClientBundle>.Ok(new GoogleDriveClientBundle
            {
                DriveService = driveService,
                ResumableUploadClient = resumableClient,
            });
        }
        catch (Exception ex)
        {
            return Result<GoogleDriveClientBundle>.Fail(
                ErrorCode.Unknown,
                "Failed to construct Google Drive client.",
                ex);
        }
    }
}
```

A private nested `GoogleAuthDelegatingHandler : DelegatingHandler` calls `userCred.GetAccessTokenForRequestAsync(ct)` per request and sets the `Authorization` header. On 401 with a fresh token, it forces a refresh and retries once. On refresh exception, it propagates — the calling provider catches and maps to `TokenRefreshFailed`.

### `FlashSkink.Core.Providers.GoogleDrive.GoogleDriveProvider` (internal sealed) — full body sketch

```csharp
internal sealed class GoogleDriveProvider : IStorageProvider, IAsyncDisposable
{
    // 1 week per Blueprint §15.2.
    private static readonly TimeSpan ResumableSessionTtl = TimeSpan.FromDays(7);

    private readonly GoogleDriveClientBundle _bundle;
    private readonly string _folderId;
    private readonly ILogger<GoogleDriveProvider> _logger;
    private int _disposed;

    public string ProviderID { get; }
    public string ProviderType => "google-drive";
    public string DisplayName { get; }

    internal GoogleDriveProvider(
        string providerId,
        string displayName,
        GoogleDriveClientBundle bundle,
        string folderId,
        ILogger<GoogleDriveProvider> logger)
    {
        ProviderID = providerId;
        DisplayName = displayName;
        _bundle = bundle;
        _folderId = folderId;
        _logger = logger;
    }

    // Each upload session lifecycle method below maps to a private helper. The public method
    // wraps with catch ordering: OperationCanceledException → Cancelled,
    // HttpRequestException → ProviderUnreachable,
    // GoogleApiException filtered by HttpStatusCode → mapped per table,
    // Exception → Unknown.
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

### `FlashSkink.Core.Providers.GoogleDrive.GoogleDriveSetup` (internal sealed) — full body sketch

```csharp
internal sealed class GoogleDriveSetup : IProviderSetup
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    private readonly IGoogleDriveClientFactory _clientFactory;
    private readonly HttpClient _tokenExchangeClient;     // Used only by ExchangeCodeAsync; not the resumable client.
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GoogleDriveSetup> _logger;

    public string ProviderType => "google-drive";
    public string DisplayName => "Google Drive";
    public ProviderSetupKind SetupKind => ProviderSetupKind.OAuth;

    // Production constructor — used by SetupAddCommand (§4.6) and BrainBackedProviderRegistry.
    public GoogleDriveSetup(ILoggerFactory loggerFactory)
        : this(new GoogleDriveClientFactory(), new HttpClient(), loggerFactory)
    {
    }

    // Test constructor — injects the factory + a configurable HttpClient.
    internal GoogleDriveSetup(
        IGoogleDriveClientFactory clientFactory,
        HttpClient tokenExchangeClient,
        ILoggerFactory loggerFactory)
    {
        _clientFactory = clientFactory;
        _tokenExchangeClient = tokenExchangeClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GoogleDriveSetup>();
    }

    public Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri, string codeChallenge, ProviderCredentials credentials, CancellationToken ct);

    public Task<Result<byte[]>> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri,
        ProviderCredentials credentials, ReadOnlyMemory<byte> dek, CancellationToken ct);

    // LocalPath methods return InvalidArgument.
    public Task<Result<ValidationResult>> ValidatePathAsync(string path, string skinkRoot, CancellationToken ct);

    public Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId, string displayName,
        byte[] encryptedToken, ProviderCredentials credentials,
        string? providerConfigJson,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);
}
```

---

## Method-body contracts

### `GoogleDriveProvider.BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()` at entry.
2. Validate `remoteName` is non-empty and `totalBytes >= 0`. On violation return `Result.Fail(InvalidArgument, "...")`.
3. Issue the initiate-resumable request. The protocol is documented in Drive's REST docs:
   ```
   POST https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=false
   Authorization: Bearer {access_token}    ← injected by the bundle's delegating handler
   Content-Type: application/json; charset=UTF-8
   X-Upload-Content-Length: {totalBytes}
   X-Upload-Content-Type: application/octet-stream

   { "name": "{leafName}", "parents": ["{_folderId}"] }
   ```
   The response is `200 OK` with a `Location:` header carrying the session URI.
4. Parse `Location`. Return `Result<UploadSession>.Ok(new UploadSession { SessionUri = <location>, ExpiresAt = UtcNow + 7d, BytesUploaded = 0, TotalBytes = totalBytes, FileID = string.Empty, ProviderID = string.Empty })`. The caller stamps `FileID` / `ProviderID` before persisting — same convention as `FileSystemProvider`.
5. The `remoteName` may contain `/` (e.g. `_witness/current.enc`, `_brain/2026-…`, `blobs/ab/cd/{uuid}.bin`). Drive's `files.insert` accepts a single name and a single parent; sharded paths must be expressed as nested folders. **Decision:** for V1, flatten the remote name — replace `/` with `_` and store as a single Drive file under `_folderId`. This matches the `FileSystemProvider`'s flat-name convention inside its staging directory. Drive's API does not expose a way to materialise the shard prefix as folders without N round-trips per shard pair, which would be a performance regression for the FileSystem-shaped name space. The `remoteId` returned by `FinaliseUploadAsync` is the Drive file ID — an opaque string — so the human-readable structure is irrelevant for download. The blueprint §15.2 description ("Google Drive | Resumable Upload | …") makes no shard claim; the §27.2 description says "Files uploaded to a dedicated folder (configurable at setup, default 'FlashSkink Backup')" — singular folder, not sharded.

**Failure paths:**

- `OperationCanceledException` → `Cancelled`.
- `HttpRequestException` (network-level) → `ProviderUnreachable`.
- HTTP `401` → trigger token refresh (the delegating handler) and retry once; on second `401` → `TokenRefreshFailed` and publish a `Critical` notification (Principle 24 — surface via `INotificationBus` if available; otherwise log at `Error`). **Notification-bus access:** §4.3 does NOT wire `GoogleDriveProvider` to `INotificationBus` directly — that wiring lives in §4.6 alongside the CLI composition root. For §4.3, `TokenRefreshFailed` is returned as a `Result.Fail` and logged at `Error`. The upload orchestrator (`UploadQueueService`) is the bus publisher per Principle 24 — it observes the `Result.Fail` and publishes.
- HTTP `403` with `reason=storageQuotaExceeded` → `ProviderQuotaExceeded`.
- HTTP `403` with `reason=userRateLimitExceeded` / `rateLimitExceeded` → `ProviderRateLimited`.
- HTTP `4xx` (other) → `UploadFailed` with the response body in `ErrorContext.Metadata`.
- HTTP `5xx` → `UploadFailed` (the retry policy in `RangeUploader` already classifies this as retryable).
- `Exception` (generic) → `Unknown`.

### `GoogleDriveProvider.UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)`

1. `ct.ThrowIfCancellationRequested()` at entry.
2. Validate: `offset >= 0`; `data.Length > 0`; `offset + data.Length <= session.TotalBytes`.
3. Build the PUT:
   ```
   PUT {session.SessionUri}
   Content-Length: {data.Length}
   Content-Range: bytes {offset}-{offset + data.Length - 1}/{session.TotalBytes}
   ```
   Body is the raw byte range. The session URI already carries Drive's upload-id token; no additional auth header is required for the session URI itself (per RFC, the session URI is single-use opaque). The bundle's `ResumableUploadClient` is used.
4. Interpret the response per Drive's docs:
   - **`308 Resume Incomplete`** — Drive accepted the range. Return `Result.Ok()`.
   - **`200 OK` or `201 Created`** — Drive accepted the final range and finalised the upload. The response body is the JSON-encoded `File` metadata; the next `FinaliseUploadAsync` call must be a no-op that returns the cached remote id. **Implementation:** cache the remote id keyed by `session.SessionUri` in a `ConcurrentDictionary<string,string> _earlyFinalised`. `FinaliseUploadAsync` checks this dictionary first.
   - **`404 Not Found`** or **`410 Gone`** — the session expired or was invalidated. Return `Result.Fail(UploadSessionExpired, ...)`.
   - **`400 Bad Request`** with response body containing `"Invalid range"` — same: `UploadSessionExpired` (Drive returns this when the session URI is stale).
   - **`401`** — same dance as `BeginUploadAsync`: refresh once, retry once, then `TokenRefreshFailed`.
   - **`403`** with quota reason → `ProviderQuotaExceeded`. Other `403` → `UploadFailed`.
   - **`4xx`** (other) → `UploadFailed`.
   - **`5xx`** → `UploadFailed`.
5. Catch ordering as in `BeginUploadAsync`.

### `GoogleDriveProvider.GetUploadedBytesAsync(UploadSession session, CancellationToken ct)`

1. Issue a PUT to `session.SessionUri` with `Content-Length: 0` and `Content-Range: bytes */{session.TotalBytes}`. This is Drive's "ask the server how much you have" idiom.
2. On `308 Resume Incomplete`, parse the `Range` response header (`bytes=0-{last}`). The uploaded byte count is `last + 1`. If no `Range` header is present, the server has 0 bytes.
3. On `200/201`, the upload is already finalised — return `Result<long>.Ok(session.TotalBytes)` so the caller takes the "no more ranges to upload" branch.
4. On `404/410` → `UploadSessionExpired`.
5. On other errors → mirror the `UploadRangeAsync` failure mapping.

### `GoogleDriveProvider.FinaliseUploadAsync(UploadSession session, CancellationToken ct)`

1. Check `_earlyFinalised` for `session.SessionUri`. If present, return the cached remote id; remove the entry.
2. Otherwise, Drive's resumable protocol finalises automatically on the last range upload returning `200/201`. If `FinaliseUploadAsync` is called and the upload is not in `_earlyFinalised`, the caller has called us out of order. **Decision:** issue a `PUT` with `Content-Range: bytes */0` empty body to query state; if the response is `200/201`, parse the file id from the JSON body. If it is `308`, the upload is incomplete — return `Result.Fail(UploadFailed, "Drive reports upload not yet complete; range PUT did not yield 200/201.")`.

This shape — "finalisation happens implicitly on the last range" — is unusual relative to Dropbox/OneDrive but consistent with Drive's documented protocol. The early-finalised cache keeps the public contract well-formed: `RangeUploader` always calls `FinaliseUploadAsync` after the last `UploadRangeAsync` succeeds, and this method always returns the remote id.

### `GoogleDriveProvider.AbortUploadAsync(UploadSession session, CancellationToken ct)`

Drive's documented abort is `DELETE {session.SessionUri}`. Returns `204 No Content` on success or `404/410` if the session is already gone. Both outcomes are mapped to `Result.Ok()` — abort is best-effort.

Failure mapping: `OperationCanceledException` → `Cancelled`; otherwise `Result.Ok()` regardless (Principle 17 spirit — best-effort cleanup is non-cancellable). Actually no: the contract says `Result<>` — we return `Ok()` on any non-cancellation outcome, log warnings on real failures, and never fail the call. The `ct` parameter exists per Principle 13 but is observed at entry only; subsequent network errors are swallowed.

### `GoogleDriveProvider.DownloadAsync(string remoteId, CancellationToken ct)`

1. Use `DriveService.Files.Get(remoteId)` with `MediaDownloader.Range` for streaming. The SDK exposes `GetMediaDownloader()` which writes into a destination stream. For our `Result<Stream>` shape, we need an `MemoryStream` or pipe-backed `Stream` we hand back to the caller.

   **Implementation:** use `_bundle.DriveService.Files.Get(remoteId).DownloadAsync(stream, ct)` requires the caller to supply the stream. Instead, use the lower-level `_bundle.DriveService.HttpClient.GetStreamAsync($"https://www.googleapis.com/drive/v3/files/{remoteId}?alt=media", ct)` which returns a `Stream` directly. The SDK's `HttpClient` is the one with auth — the URL pattern `?alt=media` is Drive's documented media-download form.

2. On `404` → `Result.Fail(BlobNotFound, ...)`.
3. On `401` → refresh / `TokenRefreshFailed`.
4. On other errors → `ProviderUnreachable` (transient) or `Unknown`.

### `GoogleDriveProvider.DeleteAsync(string remoteId, CancellationToken ct)`

`await _bundle.DriveService.Files.Delete(remoteId).ExecuteAsync(ct)`. `GoogleApiException` with `Error.Code == 404` → `Result.Ok()` (idempotent). Otherwise map per the table.

### `GoogleDriveProvider.ExistsAsync(string remoteId, CancellationToken ct)`

`await _bundle.DriveService.Files.Get(remoteId).ExecuteAsync(ct)` ignoring the response. Success → `Ok(true)`. `404` → `Ok(false)`. Other errors → fail.

### `GoogleDriveProvider.ListAsync(string prefix, CancellationToken ct)`

Drive uses a query language: `Files.List` with `Q = $"'{_folderId}' in parents and name contains '{prefix}'"`. Page through results (Drive returns `nextPageToken`). Build the result list of remote IDs.

**Caveat:** because we flatten `/` to `_` in remote names, the `prefix` argument (e.g. `_brain/`) must be re-encoded to `_brain_` for the Drive name match. The path-to-name transformation happens in a single static helper `FlattenRemoteName(string)` and the listing helper applies it to `prefix` before issuing the query.

### `GoogleDriveProvider.CheckHealthAsync(CancellationToken ct)`

A probe write under `_folderId/_health/`. Build a 1-byte payload, upload as a single non-resumable `files.insert?uploadType=media` request, delete it, return `Healthy` with the latency. On non-success outcomes return `Unreachable` (cf. `FileSystemProvider.CheckHealthAsync`'s shape).

### `GoogleDriveProvider.GetUsedBytesAsync(CancellationToken ct)` / `GetQuotaBytesAsync(CancellationToken ct)`

`await _bundle.DriveService.About.Get().Fields = "storageQuota"; .ExecuteAsync(ct)`. Returns `About.StorageQuota.Usage` (long) and `About.StorageQuota.Limit` (long? — null when unlimited e.g. Google Workspace pooled storage).

### `GoogleDriveProvider.DisposeAsync()`

Idempotent via `Interlocked.Exchange(ref _disposed, 1)`. Disposes the `GoogleDriveClientBundle` (which disposes both the `DriveService` and the `ResumableUploadClient`).

### `GoogleDriveSetup.GetAuthorizationUriAsync(redirectUri, codeChallenge, credentials, ct)`

1. `ct.ThrowIfCancellationRequested()`.
2. Validate `credentials.ClientId` is non-null and non-empty (the `ClientSecret` is not needed for the authorisation URL — only at token exchange). On violation return `Result.Fail(InvalidArgument, "Google Drive setup requires a non-null ClientId.")`.
3. Build the URL with query parameters:
   - `response_type=code`
   - `client_id={credentials.ClientId}`
   - `redirect_uri={redirectUri}`
   - `scope={DriveFileScope}` (URL-encoded)
   - `code_challenge={codeChallenge}`
   - `code_challenge_method=S256`
   - `access_type=offline` (required to receive a refresh token)
   - `prompt=consent` (forces refresh-token re-issue even for repeated authorisations — the default behaviour skips refresh-token return on subsequent runs which would silently break us)
4. Return `Result<Uri>.Ok(new Uri(authUrl))`.

### `GoogleDriveSetup.ExchangeCodeAsync(code, codeVerifier, redirectUri, credentials, dek, ct)`

1. `ct.ThrowIfCancellationRequested()`.
2. Validate `credentials.ClientId` and `credentials.ClientSecret` are both non-null.
3. POST to `TokenEndpoint` with body (form-encoded):
   - `code={code}`
   - `code_verifier={codeVerifier}`
   - `redirect_uri={redirectUri}`
   - `client_id={ClientId}`
   - `client_secret={ClientSecret}`
   - `grant_type=authorization_code`
4. Parse JSON response `{ access_token, refresh_token, expires_in, token_type, scope }`. Validate that `refresh_token` is present (Google omits it when `prompt=consent` is missed or the user already granted; the `prompt=consent` from step 3 above ensures we always get one).
5. Return `Result<byte[]>.Ok(ProviderTokenCrypto.Encrypt(refresh_token, dek))`. The access token and `expires_in` are discarded — the SDK re-derives them from the refresh token on first use.
6. Catch ordering: `OperationCanceledException` → `Cancelled`; `HttpRequestException` → `ProviderUnreachable`; JSON parse failure → `ProviderApiChanged`; HTTP error responses with body containing `"invalid_grant"` → `ProviderAuthFailed`; other HTTP errors → `Unknown`.

### `GoogleDriveSetup.ValidatePathAsync(path, skinkRoot, ct)`

Returns `Result.Ok(ValidationResult.Invalid("Google Drive is an OAuth provider; no local path is required."))`. Synchronous via `Task.FromResult`.

### `GoogleDriveSetup.CreateProviderAsync(providerId, displayName, encryptedToken, credentials, providerConfigJson, dek, ct)`

1. `ct.ThrowIfCancellationRequested()`.
2. Validate `providerId`, `displayName` non-empty.
3. Validate `credentials.ClientId` and `credentials.ClientSecret` non-null (the registry-construction caller decrypts both before calling).
4. Decrypt the refresh token:
   ```csharp
   if (!ProviderTokenCrypto.TryDecrypt(encryptedToken, dek, out var refreshToken))
       return Result<IStorageProvider>.Fail(ErrorCode.TokenRevoked,
           "Could not decrypt Google Drive refresh token; the brain may be corrupt.");
   ```
5. Build the client bundle:
   ```csharp
   var bundleResult = _clientFactory.Create(credentials.ClientId!, credentials.ClientSecret!, refreshToken, _loggerFactory);
   if (!bundleResult.Success) return Result<IStorageProvider>.Fail(bundleResult.Error!);
   ```
6. Resolve `folderId`:
   - If `providerConfigJson` is non-null and parses as `GoogleDriveProviderConfig` with a non-empty `FolderId`, use that.
   - Otherwise, look up or create the folder by name `ProviderConstants.CloudRootFolderName` under the user's My Drive root:
     - `await _bundle.DriveService.Files.List(); Q = "mimeType='application/vnd.google-apps.folder' and name='FlashSkink Backup' and 'root' in parents and trashed=false"; .ExecuteAsync(ct)`
     - If results are empty: `await _bundle.DriveService.Files.Create(new File { Name = "FlashSkink Backup", MimeType = "application/vnd.google-apps.folder" }).ExecuteAsync(ct)`.
   - The folder-resolution call site **assumes the new config will be persisted by the caller** (`SetupAddCommand` / `BrainBackedProviderRegistry`). For `BrainBackedProviderRegistry` the config is already persisted; for setup-time the caller (§4.6) updates the brain row.
7. Construct and return `Result<IStorageProvider>.Ok(new GoogleDriveProvider(providerId, displayName, bundle, folderId, _loggerFactory.CreateLogger<GoogleDriveProvider>()))`.
8. **The new (or resolved) `folderId` is not returned through `IProviderSetup.CreateProviderAsync`** — its return type is `Result<IStorageProvider>`. The caller can recover the folder id via a new internal property on `GoogleDriveProvider` (`internal string FolderId { get; }`) when persistence is required at setup time. §4.6 will use this; §4.3 makes the property available.

Catch ordering: `OperationCanceledException` → `Cancelled`; `GoogleApiException` filtered on `HttpStatusCode == Unauthorized` → `TokenRefreshFailed`; `JsonException` on config parse → `InvalidArgument`; `Exception` → `Unknown`.

### `BrainBackedProviderRegistry.TryBuildGoogleDriveAdapter(row, dek, loggerFactory, logger)`

```csharp
private static IStorageProvider? TryBuildGoogleDriveAdapter(
    ProviderRow row,
    ReadOnlyMemory<byte> dek,
    IGoogleDriveClientFactory clientFactory,
    ILoggerFactory loggerFactory,
    ILogger<BrainBackedProviderRegistry> logger)
{
    // 1. Validate the row has all the BYOC bits.
    if (row.EncryptedToken is null || row.EncryptedToken.Length == 0 ||
        row.EncryptedClientSecret is null || row.EncryptedClientSecret.Length == 0 ||
        string.IsNullOrWhiteSpace(row.ClientID))
    {
        logger.LogWarning(
            "Google Drive provider row '{ProviderId}' is missing BYOC credentials; skipping.",
            row.ProviderID);
        return null;
    }

    // 2. Decrypt the client secret using ProviderTokenCrypto. (Refresh token is decrypted later
    //    inside GoogleDriveSetup.CreateProviderAsync — the registry doesn't decrypt it here so
    //    the cleartext lives only inside the SDK client on the success path.)
    if (!ProviderTokenCrypto.TryDecrypt(row.EncryptedClientSecret, dek, out var clientSecret))
    {
        logger.LogWarning(
            "Google Drive provider row '{ProviderId}': could not decrypt client secret; skipping.",
            row.ProviderID);
        return null;
    }

    // 3. Construct the setup and call CreateProviderAsync synchronously.
    //    The cloud setup IS async (HTTP for folder lookup) so we MUST await — but TryBuildAdapter
    //    runs inside the registry's CreateAsync foreach loop, which is async. Refactor
    //    TryBuildAdapter to async: TryBuildAdapterAsync returning Task<IStorageProvider?>.
    //    See "BrainBackedProviderRegistry.CreateAsync" in "Files to modify" below.
    var setup = new GoogleDriveSetup(clientFactory, new HttpClient(), loggerFactory);
    var credentials = new ProviderCredentials { ClientId = row.ClientID, ClientSecret = clientSecret };

    var createResult = await setup.CreateProviderAsync(
        row.ProviderID, row.DisplayName, row.EncryptedToken, credentials,
        row.ProviderConfig, dek, /*ct*/ CancellationToken.None /* registry has its own ct */).ConfigureAwait(false);

    if (!createResult.Success)
    {
        logger.LogWarning(
            "Google Drive provider row '{ProviderId}' failed to construct: {Code} ({Message}); skipping.",
            row.ProviderID, createResult.Error!.Code, createResult.Error!.Message);
        return null;
    }

    return createResult.Value!;
}
```

The synchronous `TryBuildAdapter` in §4.1 becomes async. The foreach loop in `CreateAsync` already runs inside `async Task<Result<BrainBackedProviderRegistry>>`, so propagating the async signature is a mechanical change. The cleartext `clientSecret` local variable goes out of scope at the end of the helper (Principle 31 spirit — the cleartext lives only inside the constructed `DriveService`, which the bundle's `DisposeAsync` will tear down).

### `BrainBackedProviderRegistry.CreateAsync` modification

- The SELECT extends to include the three new columns:
  ```sql
  SELECT ProviderID, ProviderType, DisplayName, ClientID,
         EncryptedToken, EncryptedClientSecret, ProviderConfig
  FROM Providers
  WHERE IsActive = 1
  ORDER BY AddedUtc
  ```
- The internal `ProviderRow` record extends to carry `ClientID`, `EncryptedToken`, `EncryptedClientSecret`.
- `TryBuildAdapter` becomes `TryBuildAdapterAsync` returning `Task<IStorageProvider?>`.
- The public `CreateAsync(brain, dek, loggerFactory, ct)` overload remains unchanged externally; an internal overload `CreateAsync(brain, dek, IGoogleDriveClientFactory factory, loggerFactory, ct)` is added for tests.

---

## Integration points

- `IStorageProvider` / `UploadSession` / `ProviderHealth` / `ProviderHealthStatus` (existing public contracts) — implemented unchanged.
- `IProviderSetup` / `ProviderCredentials` / `ProviderSetupKind` / `ValidationResult` (from §4.1) — implemented unchanged.
- `IOAuthCaptureFlow` (from §4.1/§4.2) — **not consumed inside §4.3**. The setup interface receives an authorisation `code` as input; the CLI (§4.6) is the consumer of `IOAuthCaptureFlow`. `GoogleDriveSetup` only builds the authorisation URL and exchanges the code.
- `ProviderTokenCrypto` (from §4.1) — consumed in `GoogleDriveSetup.ExchangeCodeAsync` (encrypts the refresh token) and `BrainBackedProviderRegistry.TryBuildGoogleDriveAdapterAsync` (decrypts the client secret) and `GoogleDriveSetup.CreateProviderAsync` (decrypts the refresh token).
- `ProviderConstants.CloudRootFolderName` — read by `GoogleDriveSetup.CreateProviderAsync` and `GoogleDriveProviderConfig.FolderName` default.
- `BrainBackedProviderRegistry` (from §4.1) — modified per "Files to modify".
- `RangeUploader.FinaliseAndVerifyAsync` — **unchanged**. `GoogleDriveProvider` does not implement `ISupportsRemoteHashCheck`; the existing "trust the GCM tag" branch handles Drive blobs (Discrepancy 1).
- `Google.Apis.Drive.v3` (NuGet) — consumed via `DriveService`, `GoogleAuthorizationCodeFlow`, `UserCredential`, `TokenResponse`, `ClientSecrets`, `BaseClientService.Initializer`, `Google.Apis.Drive.v3.Data.File`, `GoogleApiException`.

---

## Principles touched

- **Principle 1** (Core never throws across public API boundary) — every public method on `GoogleDriveProvider`, `GoogleDriveSetup`, `GoogleDriveClientFactory` returns `Result` / `Result<T>` / direct values. Exceptions caught and wrapped at the public boundary.
- **Principle 6** (zero-knowledge at external boundaries) — Drive only ever sees AES-GCM encrypted bytes uploaded by `RangeUploader`. The refresh token is DEK-encrypted in the brain. The client secret is DEK-encrypted in the brain. Cleartext refresh token and client secret exist only inside `DriveService` (an unavoidable SDK design) for the volume's lifetime; the `DriveService` is `Dispose()`d on volume close.
- **Principle 12** (OS-agnostic) — all Drive operations work identically on Windows/Linux/macOS. The Google SDK is `netstandard2.1` and runs on every supported RID.
- **Principle 13** (`CancellationToken ct` last) — every new async method takes `ct` as the final parameter.
- **Principle 14** (`OperationCanceledException` first catch) — every async method catches `OCE` first.
- **Principle 15** (no bare `catch (Exception)` as sole catch) — every catch block has at least one specific type before the fallback.
- **Principle 17** (`CancellationToken.None` literal in compensation) — `AbortUploadAsync` issues a DELETE without `ct`-observance (best-effort cleanup). `DisposeAsync` does not accept a `ct`. The early-finalised-cache cleanup uses `CancellationToken.None` as a literal.
- **Principle 23** (provider contract frozen — additive only) — no edits to `IStorageProvider`, `UploadSession`, `ProviderHealth`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ValidationResult`. No new capability interfaces introduced in §4.3 (Discrepancy 1 — capability interfaces deferred to Phase 5).
- **Principle 24** (no silent background failures) — Drive's token-refresh failure surfaces as `Result.Fail(TokenRefreshFailed)`. The provider does not publish to `INotificationBus` directly (no bus reference); `UploadQueueService` is the publisher per Principle 24, and observes the `Result.Fail` from the provider call.
- **Principle 26** (no secrets in logs) — `GoogleDriveProvider` and `GoogleDriveSetup` never log `refreshToken`, `clientSecret`, `accessToken`, `code`, `codeVerifier`, or HTTP response bodies that may contain `access_token`/`refresh_token` fields. The token-exchange HTTP request/response is *not* logged at any level. The structured-log scope on every provider operation includes `ProviderID`, `RemoteName`, `Offset` — none secret. Drive's `md5Checksum` (logged at `Information`) is over the encrypted blob and not a secret.
- **Principle 28** (Core depends only on MEL abstractions) — every logger in §4.3 is `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions`. The Google SDK has its own internal logging but does not surface a hook into `ILogger<T>`; we don't connect them. Drive's request/response telemetry is therefore invisible at our log level, which is acceptable for V1.
- **Principle 31** (keys zeroed on volume close) — the `refreshToken` and `clientSecret` cleartext strings are minimised in scope (locals inside one method each in the registry / setup) and are dropped after the `DriveService` is constructed. The strings cannot be zeroed (`string` is interned and immutable); this is an unavoidable .NET constraint that already applies to the existing crypto code. The `DriveService` itself holds the cleartext tokens internally; it is disposed on volume close (Discrepancy 1's "the SDK clients are disposed in `BrainBackedProviderRegistry.DisposeAsync`" — already implemented in §4.1).
- **Principle 32** (no telemetry, no update checks, no network chatter) — the only network endpoints touched are Drive's documented API endpoints (under `googleapis.com` and `oauth2.googleapis.com`). No analytics, no metrics export, no version-check.

---

## Test spec

### `tests/FlashSkink.Tests/Providers/GoogleDrive/RecordingHttpMessageHandler.cs`

Reusable test handler that matches requests by (method, URL-prefix) and returns canned `HttpResponseMessage`s. Records every request for assertion. Supports:

- `Setup(HttpMethod method, string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> responder)`.
- `IReadOnlyList<HttpRequestMessage> ReceivedRequests`.
- An "unmatched request fails the test" mode.

### `tests/FlashSkink.Tests/Providers/GoogleDrive/FakeGoogleDriveClientFactory.cs`

`IGoogleDriveClientFactory` test double that constructs a real `DriveService` initialised with a custom `HttpClientFactory` whose returned `HttpClient` is backed by the supplied `RecordingHttpMessageHandler`. Also constructs the companion `ResumableUploadClient` from a separate `RecordingHttpMessageHandler`. Captures the `(clientId, clientSecret, refreshToken)` passed in so assertions can verify the credentials flowed through.

### `tests/FlashSkink.Tests/Providers/GoogleDrive/GoogleDriveProviderTests.cs` — class `GoogleDriveProviderTests`

Constructs `GoogleDriveProvider` via a private `Build()` helper that wires together the fake factory + two recording handlers (one for the SDK's `HttpClient`, one for the resumable PUT client). Tests:

**BeginUploadAsync**
- `BeginUploadAsync_HappyPath_ReturnsSessionWithLocation` — handler returns `200 OK` with `Location: https://www.googleapis.com/upload/.../resumable?upload_id=abc`. Asserts `session.SessionUri == "https://…upload_id=abc"`, `session.TotalBytes == 1024`, `session.ExpiresAt > UtcNow + 6d`.
- `BeginUploadAsync_RequestIncludesFolderIdAsParent` — assert the JSON body contains `"parents":["folder-fake-id"]`.
- `BeginUploadAsync_RequestUrlIsCorrect` — assert `POST https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable`.
- `BeginUploadAsync_FlattenedRemoteName_UsesUnderscoreForSlash` — call with `"_brain/2026-…"`; assert the JSON body's `name` field is `"_brain_2026-…"`.
- `BeginUploadAsync_NetworkFailure_ReturnsProviderUnreachable` — handler throws `HttpRequestException`; assert `Fail(ProviderUnreachable)`.
- `BeginUploadAsync_403QuotaExceeded_ReturnsProviderQuotaExceeded`.
- `BeginUploadAsync_5xx_ReturnsUploadFailed`.
- `BeginUploadAsync_Cancelled_ReturnsCancelled`.

**UploadRangeAsync**
- `UploadRangeAsync_308_ReturnsOk` — handler returns `308` with `Range: bytes=0-4194303` (4 MiB - 1); assert `Ok()`.
- `UploadRangeAsync_200_CachesEarlyFinalisation` — handler returns `200 OK` with file JSON. Assert `Ok()`. Then call `FinaliseUploadAsync` and assert the cached id is returned without an additional HTTP call.
- `UploadRangeAsync_404_ReturnsUploadSessionExpired`.
- `UploadRangeAsync_410_ReturnsUploadSessionExpired`.
- `UploadRangeAsync_400InvalidRange_ReturnsUploadSessionExpired`.
- `UploadRangeAsync_401Refreshes_RetriesOnce` — first response `401`, second `308`. Assert `Ok()` and exactly two ranges issued.
- `UploadRangeAsync_401TwiceReturnsTokenRefreshFailed`.
- `UploadRangeAsync_5xx_ReturnsUploadFailed`.
- `UploadRangeAsync_RequestContainsContentRangeHeader` — assert `Content-Range: bytes 0-4194303/8388608`.
- `UploadRangeAsync_RequestBodyMatchesRangeBytes`.

**GetUploadedBytesAsync**
- `GetUploadedBytesAsync_308_ParsesRangeHeader`.
- `GetUploadedBytesAsync_NoRangeHeader_ReturnsZero`.
- `GetUploadedBytesAsync_200_ReturnsTotalBytes`.
- `GetUploadedBytesAsync_404_ReturnsUploadSessionExpired`.

**FinaliseUploadAsync**
- `FinaliseUploadAsync_AfterEarlyFinalisation_ReturnsCachedId_NoHttp` — verify zero additional requests after the cache hit.
- `FinaliseUploadAsync_AfterAllRanges_QueriesAndReturnsId`.
- `FinaliseUploadAsync_StillIncomplete_ReturnsUploadFailed`.

**AbortUploadAsync**
- `AbortUploadAsync_204_ReturnsOk`.
- `AbortUploadAsync_404_ReturnsOk` — idempotent.
- `AbortUploadAsync_NetworkFailure_ReturnsOk_AndLogs` — swallows.

**DownloadAsync / DeleteAsync / ExistsAsync / ListAsync** — one happy-path test each plus the `404`/error branches.

**CheckHealthAsync** — happy path returns `Healthy`, latency populated; network failure returns `Unreachable`.

**GetUsedBytesAsync / GetQuotaBytesAsync** — exercise the SDK's `About` endpoint via the recording handler; assert correct fields are read from the response JSON.

**DisposeAsync** — calling twice does not throw; the bundle is disposed exactly once.

**No ISupportsRemoteHashCheck** — `Assert.False(provider is ISupportsRemoteHashCheck)` — captures Discrepancy 1 in a test so future revisions are explicit.

### `tests/FlashSkink.Tests/Providers/GoogleDrive/GoogleDriveSetupTests.cs` — class `GoogleDriveSetupTests`

- `Metadata_HasExpectedValues` — `ProviderType == "google-drive"`, `DisplayName == "Google Drive"`, `SetupKind == OAuth`.
- `GetAuthorizationUriAsync_BuildsCorrectUrl` — asserts the URL has all expected query params including `code_challenge`, `code_challenge_method=S256`, `access_type=offline`, `prompt=consent`, `scope=https://www.googleapis.com/auth/drive.file`.
- `GetAuthorizationUriAsync_MissingClientId_ReturnsInvalidArgument`.
- `GetAuthorizationUriAsync_UrlEncodesScope`.
- `ExchangeCodeAsync_HappyPath_ReturnsEncryptedRefreshToken` — handler returns `{ "access_token": "...", "refresh_token": "rt-xyz", "expires_in": 3600 }`. Decrypt the result with the same DEK and assert plaintext is `"rt-xyz"`.
- `ExchangeCodeAsync_RequestBodyContainsCodeAndVerifier` — assert form body includes `code=auth-code-1`, `code_verifier=verifier-xyz`, `client_id=...`, `client_secret=...`, `grant_type=authorization_code`, `redirect_uri=...`.
- `ExchangeCodeAsync_NetworkFailure_ReturnsProviderUnreachable`.
- `ExchangeCodeAsync_InvalidGrant_ReturnsProviderAuthFailed` — handler returns `400` with `{ "error": "invalid_grant" }`.
- `ExchangeCodeAsync_MissingRefreshToken_ReturnsProviderApiChanged` — response has `access_token` but no `refresh_token`; mapped to `ProviderApiChanged` per the contract.
- `ExchangeCodeAsync_MissingCredentials_ReturnsInvalidArgument`.
- `ExchangeCodeAsync_Cancelled_ReturnsCancelled`.
- `ValidatePathAsync_ReturnsInvalid` — Drive is OAuth, not LocalPath.
- `CreateProviderAsync_HappyPath_WithConfig_UsesPersistedFolderId` — `providerConfigJson` carries `folderId: "existing-folder-id"`; assert no Drive `files.list` or `files.create` call happens.
- `CreateProviderAsync_HappyPath_WithoutConfig_LooksUpFolder` — no config; handler returns one folder result; assert no `files.create` call.
- `CreateProviderAsync_HappyPath_WithoutConfig_NoExistingFolder_CreatesIt` — handler returns empty list, then `files.create` returns a new folder; assert both calls happen.
- `CreateProviderAsync_DecryptFails_ReturnsTokenRevoked` — pass an envelope encrypted with a different DEK.
- `CreateProviderAsync_MissingCredentials_ReturnsInvalidArgument`.
- `CreateProviderAsync_ReturnsGoogleDriveProvider_WithFolderId` — assert the constructed `IStorageProvider` is `GoogleDriveProvider` and (cast) `FolderId` matches.

### `tests/FlashSkink.Tests/Providers/BrainBackedProviderRegistryTests.cs` (extend, not duplicate)

Add five new test methods to the existing class (already covers the FileSystem dispatch arm):

- `CreateAsync_OneGoogleDriveRow_WithAllCredentials_ConstructsProvider` — inserts a Providers row with `ProviderType='google-drive'`, encrypted refresh token, encrypted client secret, ClientID, ProviderConfig with folderId. Asserts the registry contains one Google Drive provider.
- `CreateAsync_OneGoogleDriveRow_MissingEncryptedToken_LogsWarningAndSkips`.
- `CreateAsync_OneGoogleDriveRow_MissingEncryptedClientSecret_LogsWarningAndSkips`.
- `CreateAsync_OneGoogleDriveRow_DecryptClientSecretFails_LogsWarningAndSkips` — encrypted client secret blob with mismatched DEK; row is skipped.
- `CreateAsync_MixedFileSystemAndGoogleDriveRows_BothLoaded`.

The tests inject the fake `IGoogleDriveClientFactory` via the new internal `CreateAsync` overload so no real HTTP fires.

---

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (Linux, Windows, macOS Intel, macOS ARM64).
- [ ] All Phase 0–4.2 tests continue to pass.
- [ ] All new tests pass.
- [ ] `GoogleDriveProvider`, `GoogleDriveSetup`, `IGoogleDriveClientFactory`, `GoogleDriveClientFactory`, `GoogleDriveProviderConfig` exist in `src/FlashSkink.Core/Providers/GoogleDrive/`.
- [ ] `BrainBackedProviderRegistry` resolves `"google-drive"` rows to live providers when supplied a working `IGoogleDriveClientFactory`.
- [ ] `RangeUploader` is unchanged in source.
- [ ] `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ProviderHealth`, `ISupportsRemoteHashCheck` are unchanged in source.
- [ ] No new `ErrorCode` values added.
- [ ] No reference to any UI framework anywhere in the new code (Principles 8–11).
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] `Google.Apis.Drive.v3` package reference added on `FlashSkink.Core`.
- [ ] No string interpolation of secret-named fields in any logger call.
- [ ] No tests touch the real Drive API (all SDK calls are routed through `RecordingHttpMessageHandler`).

---

## Line-of-code budget

| File | Lines |
|---|---|
| `Core/Providers/GoogleDrive/GoogleDriveProvider.cs` | ~520 |
| `Core/Providers/GoogleDrive/GoogleDriveSetup.cs` | ~340 |
| `Core/Providers/GoogleDrive/IGoogleDriveClientFactory.cs` | ~30 |
| `Core/Providers/GoogleDrive/GoogleDriveClientFactory.cs` | ~140 |
| `Core/Providers/GoogleDrive/GoogleDriveProviderConfig.cs` | ~50 |
| `Core/Providers/BrainBackedProviderRegistry.cs` (delta) | ~120 |
| **src delta** | **~1,200** |
| `tests/.../GoogleDrive/GoogleDriveProviderTests.cs` | ~620 |
| `tests/.../GoogleDrive/GoogleDriveSetupTests.cs` | ~360 |
| `tests/.../GoogleDrive/FakeGoogleDriveClientFactory.cs` | ~110 |
| `tests/.../GoogleDrive/RecordingHttpMessageHandler.cs` | ~140 |
| `tests/.../Providers/BrainBackedProviderRegistryTests.cs` (delta) | ~180 |
| **test delta** | **~1,410** |
| **Total delta** | **~2,610** |

---

## Non-goals for §4.3

- Do NOT implement `ISupportsRemoteHashCheck` for `GoogleDriveProvider`. Drive uses MD5, not XXHash64 (Discrepancy 1). Cross-tail verification with provider-specific algorithms lands in Phase 5 as an additive capability interface + `RangeUploader` dispatch change.
- Do NOT modify `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, `ProviderHealth`, `ISupportsRemoteHashCheck` (Principle 23 — frozen).
- Do NOT modify `RangeUploader`, `UploadQueueService`, `BrainMirrorService` (cross-cutting decision 6 — no upload-pipeline modifications for cloud-specific quirks). The "no changes to `RangeUploader`" cross-cutting claim is preserved by choosing Option A on Discrepancy 1.
- Do NOT introduce a new `ErrorCode` value. The Drive failure-mode mappings reuse `ProviderUnreachable`, `ProviderQuotaExceeded`, `ProviderRateLimited`, `ProviderAuthFailed`, `TokenRefreshFailed`, `TokenRevoked`, `UploadFailed`, `UploadSessionExpired`, `BlobNotFound`, `ProviderApiChanged`, `Cancelled`, `InvalidArgument`, `Unknown` — all already defined.
- Do NOT implement the CLI commands `setup add` / `setup remove` / `setup list` — those are §4.6. §4.3 ships only the libraries those commands consume.
- Do NOT implement multi-account configurations (two separate Drive tails pointing at different Google accounts). V1 allows one tail per provider type; multi-account is post-V1.
- Do NOT implement automated provider-console setup. That is the paid layer (Blueprint §24.1).
- Do NOT add `INotificationBus` publish calls inside `GoogleDriveProvider`. Per Principle 24 the publisher is `UploadQueueService`, not the provider. The provider returns `Result.Fail` with the right `ErrorCode`; the queue service publishes.
- Do NOT wire `IOAuthCaptureFlow` into `GoogleDriveSetup`. The setup's role is to build the authorisation URL and exchange the code that the *caller* (§4.6 CLI) obtained via `IOAuthCaptureFlow`. Combining them would conflate two responsibilities.
- Do NOT shard the remote name space. Drive cannot trivially materialise the `{xx}/{yy}/` shard structure as nested folders without N round-trips per shard; the flat-name + flatten-`/`-to-`_` convention is the V1 approach.
- Do NOT rewire the existing `_witness/` / `_brain/` string literals in `WitnessStore` and `BrainMirrorService` to `ProviderConstants`. That rewiring is a non-goal of §4.3 too — it can land any time later as a mechanical refactor.
- Do NOT modify `Directory.Packages.props`. The `Google.Apis.Drive.v3 1.68.0.3656` pin is already in place; only the `FlashSkink.Core.csproj` reference is added.

---

## Open questions for Gate 1 review

1. **Discrepancy 1 — verification path for Drive.** Plan commits to Option A (skip `ISupportsRemoteHashCheck`; trust GCM tag). Accept, or pursue Option B (add capability interface + modify `RangeUploader` in this PR)?
2. **Folder-resolution shape — `GoogleDriveProvider.FolderId` exposed for §4.6.** §4.3 makes the folder ID available on the constructed provider so §4.6 can persist it after `SetupAddCommand` runs the first `CreateProviderAsync`. Acceptable to expose as an `internal string FolderId { get; }` property on the provider, or prefer a separate "setup result" type that §4.6 will introduce?
3. **Remote-name flattening (`/` → `_`).** Plan commits to flat naming under the `"FlashSkink Backup"` folder. Accept, or pursue nested-folders-as-shards (3 extra Drive round-trips per first upload to materialise the shard folders)?
4. **`prompt=consent` on the authorisation URL.** Forces Google to re-issue the refresh token on every authorisation, defending against the "user re-authorises and we silently miss the refresh token" failure mode. Trade-off: the user sees the consent screen each time. Accept, or take the default (refresh-token-on-first-grant-only) and document the re-authorisation pitfall?
5. **`access_type=offline`** — required to receive a refresh token. Non-negotiable; included as a one-line note for completeness.
6. **`drive.file` scope vs `drive` scope.** Plan uses `drive.file` (per-file scope — Drive can only see files our app created). This is more privacy-respecting and is the consensus best practice for non-sync apps. Accept, or use `drive` (full Drive access) to support future operations like reading user-uploaded files (out of scope for V1)?
7. **No `INotificationBus` reference in `GoogleDriveProvider`.** Principle 24's publisher is `UploadQueueService`. Confirming the design choice: the provider is bus-agnostic; the queue observes `Result.Fail` and publishes. Accept?
