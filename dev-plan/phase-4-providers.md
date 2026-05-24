# Phase 4 — Providers (FileSystem, Google Drive, Dropbox, OneDrive)

**Status marker:** This phase follows the standard session protocol defined in `CLAUDE.md`. Each section below (§4.1 through §4.6) maps to one PR, executed via `read section 4.X of the dev plan and perform`. Gate 1 (plan approval) and Gate 2 (implementation approval) are required for every section. Sections must be executed in order.

**Sequencing:** Phase 4 lands after Phase 3.5 (merged, PRs #75, #83, #85). Phase 5 (recovery, healing, verification) begins after §4.6 merges.

**Terminology note:** "BYOC" = Bring-Your-Own-Cloud. FlashSkink ships no shared OAuth app credentials. Each user supplies their own `client-id` and `client-secret` from the provider's developer console (Blueprint §24). OSS Core prints manual setup guides; automating the developer-console steps is out of scope for OSS Core and lives in the paid layer.

---

## Goal

After Phase 4:

- The three V1 cloud providers — **Google Drive**, **Dropbox**, **OneDrive** — are implemented as production `IStorageProvider` adapters and ship alongside the existing `FileSystemProvider`. Each implements the full §15.3 session lifecycle against the provider's native resumable-upload protocol.
- A single shared `IProviderSetup` contract (Blueprint §10.3) carries every provider through BYOC OAuth: print the setup guide → user obtains `client-id`/`client-secret` from the provider console → CLI launches a local-loopback OAuth listener → exchanges the auth code for a refresh token → encrypts the refresh token with the DEK → persists to the brain.
- `BrainBackedProviderRegistry` (the Phase 4 replacement for `InMemoryProviderRegistry` in production) reads `Providers` rows on every volume open, decrypts the per-row refresh token and client secret with the DEK, and constructs the appropriate cloud adapter. Tests continue to use `InMemoryProviderRegistry`; production CLI uses the brain-backed one.
- `FlashSkinkVolume.AddTailAsync` becomes a **public** method (replacing the internal `RegisterTailAsync` seam introduced in §3.6). It accepts a `TailConfiguration` describing the provider type + credentials, runs the provider's setup flow, persists the encrypted token, registers the adapter with the live registry, and writes the initial witness (§3.5.2) to the new tail.
- The CLI grows three new commands wired through `System.CommandLine`: `flashskink-cli setup guide --provider <name>`, `flashskink-cli setup add --skink <path> --provider <name> --client-id … --client-secret …`, and `flashskink-cli setup remove --skink <path> --provider <name>`. The guides themselves are embedded resources under `FlashSkink.CLI/Resources/setup-guides/`.
- **Phase 5 can start** with the full cloud-provider surface in place. The recovery, healing, and verification work depends on having multiple real provider adapters to cross-verify against — Phase 4 produces them.

---

## Cross-cutting decisions

Seven decisions span multiple PRs in this phase. Recording them here keeps the rule in one place; each section below references back.

**1. The provider contract from §10 is frozen — no edits to `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, or `ProviderHealth` in this phase.** Principle 23 (provider contract frozen at V1) applies in the inverse direction: Phase 4's job is to *consume* the frozen contract, not modify it. If a cloud-provider SDK demands a shape we did not anticipate (e.g., a third credential field), the answer is to extend `ProviderCredentials` *additively* with a new optional property — never to break an existing signature. New capability interfaces (analogous to `ISupportsRemoteHashCheck` introduced in §3.3) MAY be added if a provider needs a hook the base interface does not provide; this is the sanctioned additive-evolution path.

**2. Refresh-token encryption uses AES-256-GCM with the DEK, same envelope as the witness (§3.5.1).** A new shared helper `Core/Providers/Setup/ProviderTokenCrypto` exposes `Encrypt(string plaintext, ReadOnlyMemory<byte> dek)` / `TryDecrypt(byte[] envelope, ReadOnlyMemory<byte> dek, out string plaintext)`. Reusing the envelope keeps `Identity/WitnessCrypto` and `Providers/Setup/ProviderTokenCrypto` parallel rather than introducing a third format. The envelope is the same `[version=0x01][12-byte nonce][ciphertext][16-byte GCM tag]` shape. Refresh tokens never appear in logs (Principle 26) and never cross the public Core API boundary in plaintext — `ProviderCredentials.ClientSecret` is plaintext only inside `IProviderSetup.ExchangeCodeAsync` and `IProviderSetup.CreateProviderAsync` (the two methods the CLI calls during setup); thereafter only the encrypted blob is passed around.

**3. OAuth loopback capture is a single shared helper.** All three cloud providers use the standard RFC 8252 public-client flow with `http://127.0.0.1:<random-port>/oauth-callback` as the redirect URI. `Core/Providers/Setup/LoopbackOAuthCapture` owns the listener: it binds a random free port, launches the system browser to the consent URL, waits for the `?code=…` redirect (with a 5-minute timeout), and returns the code. Provider-specific token-exchange logic stays inside each `IProviderSetup` implementation, but the listener mechanics are not duplicated three times. The helper is `internal` to `FlashSkink.Core` and is never used by tests directly — tests inject a fake `IOAuthCaptureFlow` interface that returns a canned code without touching the network.

**4. Cloud-provider HTTP clients are constructed per-volume, not per-call.** Each cloud adapter (`GoogleDriveProvider`, `DropboxProvider`, `OneDriveProvider`) takes the underlying SDK's client (`DriveService`, `DropboxClient`, `GraphServiceClient`) in its constructor. The client is constructed once in `BrainBackedProviderRegistry.GetAsync` when the registry first resolves the provider, then cached for the volume's lifetime. Token refresh is the SDK's responsibility; our adapter observes refresh failure and surfaces `ErrorCode.TokenRefreshFailed` (Principle 24 — published to the notification bus). On `DisposeAsync` of the volume, the registry disposes every cached adapter, which disposes the underlying SDK client (and its `HttpClient`).

**5. Per-provider remote layout is fixed at V1 and lives in `ProviderConstants`.** Each cloud provider stores blobs under a top-level folder (`"FlashSkink Backup"` for Drive/Dropbox/OneDrive; configurable only at setup time, persisted in `Providers.ProviderConfig` JSON). The witness path `_witness/current.enc` (§3.5.1) and the brain-mirror path `_brain/{timestamp}.bin` (§3.5 brain-mirror service) are subpaths of that root. Sharded blob paths (`{xx}/{yy}/{blob}.bin`) follow the same scheme as `FileSystemProvider`. Changing the layout post-V1 would orphan all previously-uploaded blobs, so V1 commits to this scheme via `ProviderConstants.RootFolderName = "FlashSkink Backup"` and the layout constants live in `Core/Providers/ProviderConstants.cs`.

**6. Provider-specific verification follows §15.7's table exactly.** `GoogleDriveProvider` implements `ISupportsRemoteHashCheck` by computing MD5 over the local blob lazily and comparing to Drive's `md5Checksum`. `DropboxProvider` compares blob size and Dropbox's content-hash. `OneDriveProvider` reads the SHA1/quickXorHash from Graph's metadata response. None of these match `FileSystemProvider`'s XXHash64-of-re-read approach; the verification step inside `RangeUploader` dispatches on the capability interface, not on a hard-coded algorithm. No changes to `RangeUploader` are required — Phase 3 already structured it for this dispatch.

**7. CLI setup commands live in `FlashSkink.CLI/Commands/Setup/`.** Three command classes: `SetupGuideCommand`, `SetupAddCommand`, `SetupRemoveCommand`. A fourth `SetupListCommand` shows the configured tails for a skink. All four are wired into the root `System.CommandLine` command tree in `Program.cs`. The "file operations" CLI surface (`flashskink-cli write`, `read`, `ls`, `daemon`, `status`, `logs`) is **NOT** in scope for Phase 4 — that is Phase 6. Phase 4's CLI footprint is strictly the setup commands plus the supporting volume-open/close plumbing those commands need.

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §4.1 | Setup contract, token crypto, brain-backed registry | `ProviderTokenCrypto` (DEK envelope for refresh tokens), `BrainBackedProviderRegistry` (production replacement for `InMemoryProviderRegistry`), `ProviderConstants` (root folder, brain/witness subpath constants), `IOAuthCaptureFlow` abstraction; `FileSystemProviderSetup` (the no-OAuth `LocalPath` setup) as the first `IProviderSetup` implementation; tests |
| §4.2 | OAuth loopback capture | `LoopbackOAuthCapture`, port allocation, browser launch, 5-minute timeout, PKCE verifier generation; fake `IOAuthCaptureFlow` for tests; integration test with a stub authorisation server |
| §4.3 | Google Drive provider and setup | `GoogleDriveProvider` (full §10.1 surface), `GoogleDriveSetup` (full §10.3 surface), `Google.Apis.Drive.v3` wiring, resumable upload via Drive's protocol, `md5Checksum`-based hash check; tests against a recorded SDK fake |
| §4.4 | Dropbox provider and setup | `DropboxProvider`, `DropboxSetup`, `Dropbox.Api` wiring, `upload_session/start`/`append_v2`/`finish` lifecycle, 48 h session-TTL handling, content-hash verification; tests |
| §4.5 | OneDrive provider and setup | `OneDriveProvider`, `OneDriveSetup`, `Microsoft.Graph` wiring, `createUploadSession` + PUT range, expiry handling, SHA1/quickXorHash verification; tests |
| §4.6 | CLI setup commands + public `AddTailAsync` | `SetupGuideCommand`, `SetupAddCommand`, `SetupRemoveCommand`, `SetupListCommand`; public `FlashSkinkVolume.AddTailAsync` (replaces internal `RegisterTailAsync`); embedded `Resources/setup-guides/{provider}.txt`; end-to-end test using `FaultInjectingStorageProvider` + a fake `IOAuthCaptureFlow` |

Full implementation detail for each section lives in `.claude/plans/pr-4.X.md`, written at Gate 1 of the corresponding session. The notes below summarise the blueprint sections each PR must read and the NuGet packages it introduces.

---

## Section notes

### §4.1 — Setup contract, token crypto, brain-backed registry

**Blueprint sections to read:** §10.3 (`IProviderSetup`), §10.4 (`ProviderHealth`), §16.2 (`Providers` table — `EncryptedRefreshToken`, `EncryptedClientSecret`, `ProviderConfig` columns), §24.3 (the two-stage CLI flow — the contract this PR sets up downstream consumers for), §27.1 (FileSystem setup is trivial — no OAuth, just path validation).

**Scope summary:** Three new files in `Core/Providers/Setup/`: `ProviderTokenCrypto.cs`, `BrainBackedProviderRegistry.cs`, `FileSystemProviderSetup.cs`. One new file in `Core/Providers/`: `ProviderConstants.cs`. One new abstraction in `Core.Abstractions/Providers/`: `IOAuthCaptureFlow.cs` (the seam §4.2 implements). One modification to `FlashSkinkVolume.cs`: the registry-construction path in `BuildVolumeFromSessionAsync` switches to `BrainBackedProviderRegistry` when `options.ProviderRegistry` is null in *production* code paths (tests continue to pass an `InMemoryProviderRegistry` explicitly).

**Key constraints:**
- `BrainBackedProviderRegistry` reads `Providers` rows once at volume open and caches the constructed adapters for the volume lifetime. It does NOT re-read on every `GetAsync` call — the registry holds the live adapters, and `AddTailAsync`/`RemoveTailAsync` mutate the cache in lock-step with the brain row. Re-reading per call would defeat per-call latency.
- `FileSystemProviderSetup.ValidatePathAsync` rejects paths that are subdirectories of the skink (Blueprint §24.3 — would create a backup loop). It also rejects paths that don't exist or aren't writable.
- Production code paths use `BrainBackedProviderRegistry`. Tests construct `InMemoryProviderRegistry` directly and pass via `VolumeCreationOptions.ProviderRegistry` (the §3.5.2 test pattern continues to work).
- `ProviderTokenCrypto` is `internal` — refresh tokens never cross the `FlashSkink.Core` public API boundary in plaintext. The CLI's `SetupAddCommand` calls `IProviderSetup.ExchangeCodeAsync` (which returns an already-encrypted byte array) and persists the bytes via `FlashSkinkVolume.AddTailAsync`.

**NuGet:** None new (`Microsoft.Data.Sqlite`, `Dapper`, and `System.Security.Cryptography.AesGcm` from BCL all reused).

---

### §4.2 — OAuth loopback capture

**Blueprint sections to read:** §24.5 (Local-loopback OAuth capture — the canonical description), §24.3 (the two-stage flow — what the helper enables), §3 (Zero trust in the host — no host-side state from the OAuth dance).

**Scope summary:** `Core/Providers/Setup/LoopbackOAuthCapture.cs` implementing `IOAuthCaptureFlow`. The helper:
1. Allocates a random free port via `TcpListener(IPAddress.Loopback, 0)` then immediately closes the listener to read the assigned port (the standard "free port" idiom).
2. Starts an `HttpListener` on `http://127.0.0.1:{port}/oauth-callback`.
3. Launches the system browser to the consent URL passed in by the caller (using `Process.Start` with `UseShellExecute = true`; OS-specific branching only for the launcher — the listener itself is identical on every OS, Principle 12).
4. Awaits the redirect (5-minute timeout, surfaced as `Result.Fail(ErrorCode.Timeout)`).
5. Extracts `?code=…` from the request URL, writes a small HTML success page back to the browser, stops the listener, returns the code.

**Key constraints:**
- PKCE (RFC 7636) is generated by the helper: `code_verifier` (random 64-byte base64url) and `code_challenge` (SHA-256 of verifier). The challenge is included in the authorisation URL the caller builds; the verifier is returned alongside the code so the per-provider `ExchangeCodeAsync` can include it in the token-exchange request.
- The helper accepts a cancellation token; on cancellation it disposes the listener and returns `Cancelled` (Principle 14).
- The `HttpListener` is bound only to `127.0.0.1` — never to `0.0.0.0` — so an external attacker on the same network cannot intercept the code.
- Tests do NOT touch the real network. The `IOAuthCaptureFlow` interface is the seam; a `FakeOAuthCaptureFlow` test double returns a canned code immediately. The integration test for the real helper spins up a stub `HttpListener` representing the provider's authorisation server and exercises the full flow against `http://127.0.0.1:…`.

**NuGet:** None new (`System.Net.HttpListener`, `System.Diagnostics.Process`, `System.Security.Cryptography.RandomNumberGenerator` all in BCL).

---

### §4.3 — Google Drive provider and setup

**Blueprint sections to read:** §10.1–10.4 (provider contract), §15.2 (Drive resumable-upload protocol mapping), §15.7 (Drive verification — `md5Checksum`), §24.3 (Drive setup walkthrough), §27.2 (Drive provider notes).

**Scope summary:** `Core/Providers/GoogleDrive/GoogleDriveProvider.cs` (the `IStorageProvider` adapter), `Core/Providers/GoogleDrive/GoogleDriveSetup.cs` (the `IProviderSetup` adapter), `Core/Providers/GoogleDrive/GoogleDriveClientFactory.cs` (constructs `DriveService` from credentials + decrypted refresh token).

**Algorithm — `GoogleDriveProvider.BeginUploadAsync`:**
1. Construct a `FilesResource.CreateMediaUpload` with the file metadata (name from `remoteName`, parents = configured root folder ID).
2. Issue the "initiate resumable" request via the SDK's `IUploadProgress` API or the lower-level `ResumableUpload` API.
3. Capture the session URI from the response's `Location` header.
4. Return `UploadSession { SessionUri = <uri>, ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromDays(7), … }` (Drive resumable sessions are valid for 1 week per §15.2).

**Algorithm — `UploadRangeAsync`:**
1. PUT the range bytes to the session URI with `Content-Range: bytes {offset}-{offset+len-1}/{totalBytes}`.
2. On 308 Resume Incomplete: success for this range; the next range follows.
3. On 200/201: the upload finalised early (Drive accepted the last range and returned the file metadata in the response — this is a normal Drive behaviour). Treat as finalisation; the next `FinaliseUploadAsync` call is a no-op that returns the cached RemoteId.
4. On 4xx with specific error codes for expired session: return `Fail(UploadSessionExpired)`.
5. On 5xx: return `Fail(UploadFailed)` with the response body in `ErrorContext.Metadata`.

**Setup — `GoogleDriveSetup`:**
- `SetupKind = OAuth`.
- `GetAuthorizationUriAsync` constructs the Google OAuth 2.0 consent URL with scope `https://www.googleapis.com/auth/drive.file` (per-file scope, not full-drive), the user's `clientId`, `redirect_uri=http://127.0.0.1:{port}/oauth-callback`, and the PKCE challenge.
- `ExchangeCodeAsync` POSTs to `https://oauth2.googleapis.com/token` with the code, verifier, redirect URI, client credentials; receives `{access_token, refresh_token, expires_in}`. Returns `ProviderTokenCrypto.Encrypt(refresh_token, dek)`.
- `CreateProviderAsync` decrypts the refresh token, constructs a `UserCredential` via the SDK's `GoogleAuthorizationCodeFlow`, constructs a `DriveService`, wraps it in `GoogleDriveProvider`. Also creates the `"FlashSkink Backup"` folder on first call (idempotent — checks for existence first) and stashes the folder ID in `ProviderConfig`.

**NuGet:** `Google.Apis.Drive.v3` (latest stable at section start, central-package-managed; version chosen by verifying the package targets `net10.0` or `netstandard2.1`).

**Key constraints:**
- The Drive client's internal `HttpClient` is reused across calls (per cross-cutting decision 4). Disposing the provider disposes the underlying `DriveService`.
- Token refresh: the SDK's `UserCredential.RefreshTokenAsync` is invoked lazily. On refresh failure (revoked/expired refresh token), return `Fail(TokenRefreshFailed)` and publish a `Critical` notification (Principle 24).
- The MD5 hash check in §15.7 is implemented as `ISupportsRemoteHashCheck.GetRemoteHashAsync`. The local-blob MD5 is computed lazily inside `RangeUploader` (which already dispatches on the capability interface — no changes to `RangeUploader`).

---

### §4.4 — Dropbox provider and setup

**Blueprint sections to read:** §10.1–10.4, §15.2 (Dropbox upload-sessions protocol), §15.4 (48 h session-TTL handling — Dropbox is the canonical case), §15.7 (Dropbox verification — content-hash), §24.3 (Dropbox setup walkthrough), §27.3 (Dropbox provider notes).

**Scope summary:** `Core/Providers/Dropbox/DropboxProvider.cs`, `Core/Providers/Dropbox/DropboxSetup.cs`, `Core/Providers/Dropbox/DropboxClientFactory.cs`. Same shape as §4.3 with Dropbox-specific protocol.

**Protocol mapping:**
- `BeginUploadAsync` → `Files.UploadSessionStartAsync` returns a session ID. `ExpiresAt = UtcNow + 48h` per §15.2.
- `UploadRangeAsync` → `Files.UploadSessionAppendV2Async` with offset checking.
- `FinaliseUploadAsync` → `Files.UploadSessionFinishAsync` with the final path under the configured root.
- `AbortUploadAsync` → Dropbox does not support explicit abort; the session expires naturally after 48 h. The method is a no-op that returns `Ok` (logged at `Debug`).

**Verification:** `ISupportsRemoteHashCheck` uses Dropbox's content-hash (4 MB blocks SHA256ed, concatenated and SHA256ed again — the §15.7 row's "content-hash for additional verification" path). The algorithm is documented in Dropbox's developer docs; implement it locally to compute the expected hash from the on-disk blob, then compare to `FileMetadata.ContentHash`.

**NuGet:** `Dropbox.Api` (central-package-managed; verify `net10.0` compatibility).

**Key constraints:**
- The Dropbox SDK's `DropboxClient` is constructed with the refresh token; the SDK manages access-token refresh internally.
- On 401 from any call: return `Fail(TokenExpired)` if the access token expired (the SDK's auto-refresh should handle this; failure means the refresh token itself is bad) or `Fail(TokenRevoked)` based on the response body.
- The 48 h TTL handling is the canonical test of `RangeUploader`'s expiry-restart path (§3.3): the fault-injection test for Dropbox specifically exercises "session expires mid-upload, worker restarts from byte 0, finalises successfully on the second pass."

---

### §4.5 — OneDrive provider and setup

**Blueprint sections to read:** §10.1–10.4, §15.2 (OneDrive `createUploadSession` protocol), §15.7 (OneDrive verification), §24.3 (OneDrive setup walkthrough), §27.4 (OneDrive provider notes).

**Scope summary:** `Core/Providers/OneDrive/OneDriveProvider.cs`, `Core/Providers/OneDrive/OneDriveSetup.cs`, `Core/Providers/OneDrive/OneDriveClientFactory.cs`.

**Protocol mapping:**
- `BeginUploadAsync` → Graph `POST /me/drive/root:/FlashSkink Backup/{remoteName}:/createUploadSession` returns an upload URL with an `expirationDateTime`.
- `UploadRangeAsync` → PUT to the upload URL with `Content-Range: bytes {offset}-{end}/{total}` (same shape as Drive's resumable PUT — these two providers share a wire format even though their initiation differs).
- `FinaliseUploadAsync` → The last PUT returns the `DriveItem` metadata directly; no separate finalise call. The RemoteId is the `DriveItem.Id` from the response.
- `AbortUploadAsync` → `DELETE` the upload URL.

**Verification:** OneDrive provides both `sha1Hash` and `quickXorHash` in the file metadata. Use `quickXorHash` per Microsoft's recommendation for consumer OneDrive (SHA1 is provided but Microsoft is phasing it out). Implement the QuickXorHash algorithm locally (well-documented; ~50 lines of code).

**NuGet:** `Microsoft.Graph` (central-package-managed; verify `net10.0` compatibility).

**Key constraints:**
- OneDrive's `expirationDateTime` is parsed and stored on the `UploadSession.ExpiresAt` field.
- The Graph SDK constructs an `HttpClient` internally; reuse per cross-cutting decision 4.
- Setup uses Microsoft identity platform v2.0 endpoints (`https://login.microsoftonline.com/common/oauth2/v2.0/authorize` and `/token`) with scope `Files.ReadWrite` and `offline_access` (the latter is required to receive a refresh token).

---

### §4.6 — CLI setup commands + public `AddTailAsync`

**Blueprint sections to read:** §11 (`FlashSkinkVolume` public API — `AddTailAsync`, `RemoveTailAsync`, `ListTailsAsync`), §24.3 (two-stage CLI flow), §24.4 (where the guides live — embedded resources), §25 (CLI vocabulary — strictly user-facing, no "tail provider" / "DEK" / "encrypted refresh token" leakage; user sees "Google Drive", "Dropbox", "OneDrive", "folder").

**Scope summary:**
- Promote `FlashSkinkVolume.RegisterTailAsync` (internal, from §3.6) to a public `AddTailAsync(TailConfiguration config, CancellationToken ct)`. The signature follows Blueprint §11. The implementation calls into the provider's `IProviderSetup` flow (token exchange happens inside `AddTailAsync` when `config.AuthorizationCode` is set; for `LocalPath` providers the code is null and only `ValidatePathAsync` runs).
- `RemoveTailAsync(string providerId, CancellationToken ct)` — public method per Blueprint §11. Deletes the `Providers` row, the in-memory registry entry, and any pending `UploadSessions` / `TailUploads` rows for that provider. Does NOT delete the remote data — the user is responsible for cleaning up their cloud account if they want to (CLI prints a reminder).
- `ListTailsAsync(CancellationToken ct)` — public method per Blueprint §11. Returns a `TailInfo` per row including last successful upload time, last known health, byte counts.
- `CLI/Commands/Setup/SetupGuideCommand.cs` — reads the embedded resource `Resources/setup-guides/{provider}.txt` and prints it to stdout. No skink open required; this command works without any `--skink` argument.
- `CLI/Commands/Setup/SetupAddCommand.cs` — opens the skink with the supplied password, calls `IOAuthCaptureFlow` for the cloud providers (or `ValidatePathAsync` for FileSystem), then calls `volume.AddTailAsync(...)`. Prints a user-vocabulary success message ("✓ Google Drive tail configured" — Principle 25).
- `CLI/Commands/Setup/SetupRemoveCommand.cs` — opens the skink, calls `volume.RemoveTailAsync(providerId)`, prints success. Confirmation prompt unless `--yes` is passed.
- `CLI/Commands/Setup/SetupListCommand.cs` — opens the skink, calls `volume.ListTailsAsync()`, prints a table.
- `CLI/Resources/setup-guides/google-drive.txt`, `dropbox.txt`, `onedrive.txt`, `filesystem.txt` — the printed guides. Marked as `EmbeddedResource` in the `.csproj`.

**Key constraints:**
- The internal `RegisterTailAsync` method introduced in §3.6 is **deleted** in this PR. All call sites (tests, the new `AddTailAsync` implementation) move to the public surface. The conversion is mechanical — the signatures are identical apart from visibility.
- The CLI never logs or echoes refresh tokens, client secrets, or authorization codes (Principle 26). The `--client-secret` argument is read but never written back to stdout or to the log file. The CLI's argument parser is configured to mark `client-secret` as "sensitive" for `System.CommandLine`'s tracing.
- The end-to-end test exercises the full flow: `flashskink-cli volume create` → `setup add --provider filesystem` → write a file → confirm it lands on the FileSystem tail (using a per-test temp dir as the tail's root). The cloud-provider end-to-end is exercised via `FaultInjectingStorageProvider` wrapping a `FileSystemProvider` with the fake `IOAuthCaptureFlow` returning a canned token — the SDK adapters themselves are not in the integration loop (their unit tests in §4.3–4.5 cover the SDK boundary).
- The CLI's existing `Program.cs` composition root gains the `BrainBackedProviderRegistry` wiring and the Serilog enrichment that includes `ProviderID` on every log scope inside the upload pipeline.

**NuGet:** None new (`System.CommandLine` from Phase 0 is reused; the SDK packages from §4.3–4.5 are reused).

---

## Acceptance criteria for the phase as a whole

- [ ] Builds with zero warnings on all targets (Linux, Windows, macOS Intel, macOS ARM64).
- [ ] All Phase 0–3.5 tests continue to pass.
- [ ] `dotnet test` includes new unit tests for every new public type and every new CLI command.
- [ ] End-to-end test in §4.6 exercises `setup add` → write file → verify the file lands on at least one configured tail.
- [ ] No reference to `Avalonia.*`, `System.Windows.*`, `Microsoft.Maui.*`, `Microsoft.UI.*`, `CommunityToolkit.Mvvm`, `FlashSkink.Presentation`, or `FlashSkink.UI.*` anywhere in the new code (Principles 8–11; verified by `ArchitectureTests.cs`).
- [ ] All new logger calls are scoped (no string interpolation of secret-named fields; verified by the existing `LoggingHygieneTests.cs` and the CI lint).
- [ ] Witness writes (§3.5.2) are exercised against the new cloud adapters in unit tests (against the SDK fakes) — the witness contract holds across all four providers.
- [ ] No new `ErrorCode` values added (Phase 3 cross-cutting decision 2 continues to apply unless a real cloud-provider failure mode requires one; in that case the addition is justified in the PR's plan document under "Non-goals" → moved to "In scope").

---

## Non-goals for the phase as a whole

- Do NOT implement file-operation CLI commands (`write`, `read`, `ls`, `daemon`, `status`, `logs`). Those are Phase 6.
- Do NOT implement healing or cross-tail verification logic. Those are Phase 5.
- Do NOT implement adaptive range sizing, per-tail concurrency tuning, or upload-bandwidth throttling. Post-V1.
- Do NOT implement multi-account or multi-folder configurations within a single provider type (e.g., two separate Google Drive tails pointing at different Google accounts). V1 allows one tail per provider type; multi-account is post-V1.
- Do NOT implement automated provider-console setup (creating the OAuth app on Google/Dropbox/Microsoft's behalf). That is the paid layer's domain (Blueprint §24.1).
- Do NOT modify `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderCredentials`, `ProviderSetupKind`, or `ProviderHealth` (Principle 23 — the contract is frozen). New capability interfaces analogous to `ISupportsRemoteHashCheck` MAY be added if a provider needs one; this is the additive-evolution exception.
- Do NOT modify `RangeUploader`, `UploadQueueService`, or `BrainMirrorService` to accommodate cloud-specific quirks. All cloud quirks live behind the `IStorageProvider` surface; if a quirk leaks past it, the right answer is an additive capability interface, not a queue-service modification.
