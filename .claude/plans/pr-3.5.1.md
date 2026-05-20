# PR 3.5.1 — Witness foundation

**Branch:** `pr/3.5.1-witness-foundation`
**Blueprint sections:** §18.6 (DEK lifecycle), §19.5 (single-instance lock — pattern reference for two-file separation), §31.1 (VolumeID), §31.2–31.3 (app version metadata, brain version stamping). New blueprint sections (§19.6–19.8) land in §3.5.2 / §3.5.3 — not in this PR.
**Dev plan section:** [phase-3.5 §3.5.1](../../dev-plan/phase-3.5-volume-identity-and-clone-safety.md) — Witness foundation. The phase-level dev-plan file itself (`dev-plan/phase-3.5-volume-identity-and-clone-safety.md`) is currently untracked and ships in this PR as the first concrete commit referencing it.

---

## Scope

Plumbing-only PR. Introduces the per-volume `VolumeEpoch` counter, the `VolumeState` enum (Normal / Fenced — the enforcement lands in §3.5.2 but the enum is needed here so `BackfillAndStampOnOpenAsync`'s return type widens in one step), a `WitnessPayload` value type with serialize/parse, a `WitnessCrypto` helper for AES-256-GCM envelope encryption, and a `WitnessStore` service that reads and writes the encrypted witness file on a tail through `IStorageProvider`. Two new `ErrorCode` values reserve the codes that §3.5.2 and §3.5.3 will use.

No handshake logic, no fenced-state enforcement, no split-brain detection, no `PromoteAsync` in this PR. The end-to-end conflict-detection story arrives in §3.5.2; this PR is the foundation that PR can call into. Verifiable by tests: encrypt/decrypt round-trips, payload JSON round-trips, `WitnessStore.WriteAsync` followed by `TryReadAsync` recovers the payload, and `VolumeEpoch` is correctly seeded and incremented across open/close cycles.

One ancillary change to `FileSystemProvider` is needed: the existing `ComputeRemotePath` recognizes only the `_brain_` prefix and routes everything else to the sharded `blobs/{xx}/{yy}/` layout. The witness file needs to land at `{rootPath}/_witness/current.enc` (parallel to `_brain/`) so that `provider.ListAsync("_witness/")` finds it. Adding the prefix branch is ~5 lines of additive code, matching the existing `_brain_` pattern; this is documented inline as Change 5 below.

Decision pinned in this PR: **VolumeEpoch is seeded at `"1"`, not `"0"`-then-incremented.** The dev-plan text suggests both `CreateAsync` and `OpenAsync` should increment, but the current `CreateAsync` does not call `BackfillAndStampOnOpenAsync`. Seeding `"1"` directly produces the same observable behaviour (first session = epoch 1, second session = epoch 2) without modifying `CreateAsync`'s flow. The §3.5.2 handshake only runs in `OpenAsync` anyway (a brand-new volume has no registered tails), so the simplification is invisible at the protocol layer.

---

## Files to create

- `src/FlashSkink.Core/Identity/VolumeState.cs` — ~30 lines. `public enum VolumeState { Normal = 0, Fenced = 1 }` with XML doc cross-referencing §3.5.3's `PromoteAsync` and Blueprint §19.7 (both arrive later; the references are forward placeholders).
- `src/FlashSkink.Core/Identity/WitnessPayload.cs` — ~110 lines. `internal readonly record struct` with seven fields, `ForNewSession` factory, `SerializeUtf8` / `TryParse` (ReadOnlySpan and byte[]), `ToDisplayString`.
- `src/FlashSkink.Core/Identity/WitnessCrypto.cs` — ~90 lines. `internal static class`. AES-256-GCM with a 1-byte version header, 12-byte nonce, ciphertext, 16-byte tag. `Encrypt`/`TryDecrypt`. Never throws.
- `src/FlashSkink.Core/Identity/WitnessStore.cs` — ~180 lines. `internal sealed class` taking `ILogger<WitnessStore>` at construction; `TryReadAsync` and `WriteAsync` accept the provider and DEK as per-call parameters (no long-lived key references — Principle 31 hygiene).
- `tests/FlashSkink.Tests/Identity/VolumeStateRoundTripTests.cs` — ~60 lines. Sanity tests for the enum (parse from string, ToString stability) — these would otherwise live inside `VolumeEpochTests` but keeping them separate documents the enum's contract.
- `tests/FlashSkink.Tests/Identity/WitnessPayloadTests.cs` — ~110 lines. Serialize round-trip, TryParse edge cases, ToDisplayString format.
- `tests/FlashSkink.Tests/Identity/WitnessCryptoTests.cs` — ~120 lines. Encrypt-then-decrypt, wrong DEK, tampered ciphertext, truncated envelope, wrong version byte, nonce randomness check.
- `tests/FlashSkink.Tests/Identity/WitnessStoreTests.cs` — ~180 lines. Uses `FileSystemProvider` and `FaultInjectingStorageProvider`; tests read/write round-trip, replacement of existing witness, corrupted file → null, wrong DEK → null, cancellation, unreachable provider → null.
- `tests/FlashSkink.Tests/Identity/VolumeEpochTests.cs` — ~140 lines. Full integration via `FlashSkinkVolume.CreateAsync`/`OpenAsync`, asserting epoch seed = 1, increment to 2/3 across reopens, backfill to next-increment value when legacy row missing, plus `VolumeState` read paths (absent row → Normal; corrupted value → Normal; "Fenced" round-trips).

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — three localised changes:
  1. `SeedInitialSettingsAsync`: add one upsert for `VolumeEpoch = "1"` inside the existing transaction. Insert position: after the `VolumeID` row, before `AppVersionCreatedWith`. No reformatting of surrounding code.
  2. `BackfillAndStampOnOpenAsync`: change return type from `Task<Result>` to **`Task<Result<(VolumeState State, long NewEpoch)>>`**. Inside the existing transaction (after step 3 — the `AppVersionLastOpened` upsert), add: read `Settings["VolumeState"]` (absent → `Normal`; unparseable → `Normal`); read `Settings["VolumeEpoch"]` (absent → `0`); compute `newEpoch = currentEpoch + 1`; upsert the new value. Return `Result.Ok((state, newEpoch))`. The fenced-state row is NOT written here — only read.
  3. Update the single call site in `OpenAsync` to receive the tuple. The values are not consumed yet (no handshake in this PR); store them in local variables prefixed with an underscore comment `// consumed by §3.5.2 handshake` and discard. (Acceptable in §3.5.1 because the next PR adds the consumer; flagged so a Gate 2 reviewer doesn't read it as an unused-value bug.)
- `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` — add a new `// ── Clone safety ──` block at the end of the enum with two new values:
  - `SplitBrainDetected` — XML doc explains that a tail's witness epoch is higher than the local brain's epoch (reserved by §3.5.1 for §3.5.2 to use; the code itself is dead in this PR but the enum value reserves a stable place in the enum).
  - `VolumeFenced` — XML doc explains that Phase 2 uploads are blocked while the volume is fenced (reserved similarly; consumed in §3.5.2).
- `src/FlashSkink.Core/Providers/FileSystemProvider.cs` — **Change 5**: in `ComputeRemotePath`, add a `_witness_` prefix branch parallel to the existing `_brain_` branch. ~5 lines. Witness files land at `{rootPath}/_witness/{rest}`. Document in the method's XML summary.
- `tests/FlashSkink.Tests/Providers/FaultInjectingStorageProvider.cs` — add two new fault knobs:
  - `FailNextList(ErrorCode code)` — next `ListAsync` call returns `Fail(code)`. Default code on the parameterless overload: `ProviderUnreachable`.
  - `FailNextDownload(ErrorCode code)` — next `DownloadAsync` returns `Fail(code)`. Default: `ProviderUnreachable`.
  Both follow the same single-shot pattern as `FailNextRange` / `FailNextBegin` / `FailNextFinalise`. Used by `WitnessStoreTests`.
- `dev-plan/phase-3.5-volume-identity-and-clone-safety.md` — already on disk, untracked; `git add`'d into this PR's commit. No content changes from the revised plan committed during planning.

## Dependencies

- NuGet: none added. `System.Security.Cryptography.AesGcm` and `System.Text.Json` ship in the .NET 10 BCL.
- Project references: none added.

---

## Public API surface

No new public types on `Core` or `Core.Abstractions`. The only public addition is:

- **`FlashSkink.Core.Identity.VolumeState`** (`public enum`):
  - `Normal = 0`
  - `Fenced = 1`

`VolumeState` is `public` so that `FlashSkinkVolume.State` (added in §3.5.2) can expose it without an extra wrapper. Everything else in `Identity/` is `internal` per Phase 3.5 scope.

The two new `ErrorCode` values are public additions to a public enum but they don't change any existing handler logic — they reserve future codes. No handler updates in this PR.

---

## Internal types

### `FlashSkink.Core.Identity.WitnessPayload` (internal readonly record struct)

```csharp
internal readonly record struct WitnessPayload(
    string VolumeId,
    long Epoch,
    string SessionId,
    string CommittedAtUtc,
    string Host,
    string AppVersion,
    bool ConflictObserved);
```

JSON field names (camelCase, matching `InstanceLockManifest`): `volumeId`, `epoch`, `sessionId`, `committedAtUtc`, `host`, `appVersion`, `conflictObserved`.

**Methods:**
- `internal static WitnessPayload ForNewSession(string volumeId, long epoch, string appVersion)` — factory.
- `internal byte[] SerializeUtf8()` — compact JSON via `System.Text.Json.JsonSerializer.SerializeToUtf8Bytes`.
- `internal static bool TryParse(ReadOnlySpan<byte> utf8, out WitnessPayload payload)` — forgiving; returns `false` for empty/non-JSON/non-object-root. Unknown JSON fields ignored; missing `conflictObserved` defaults to `false`.
- `internal static bool TryParse(byte[] utf8, out WitnessPayload payload)` — array overload that forwards to the span version.
- `internal string ToDisplayString()` — `"epoch={Epoch}, host={Host}, session={SessionId}, appVersion={AppVersion}"`.

### `FlashSkink.Core.Identity.WitnessCrypto` (internal static class)

```csharp
internal static class WitnessCrypto
{
    internal const byte CurrentVersion = 0x01;
    internal const int NonceSize = 12;   // AesGcm.NonceByteSizes default
    internal const int TagSize = 16;     // AesGcm.TagByteSizes default

    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dek);
    internal static bool TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> dek, out byte[] plaintext);
}
```

**Envelope:** `[1 byte version][12 byte nonce][N byte ciphertext][16 byte tag]`. AAD is empty span (the VolumeId binding lives inside the plaintext and is checked by `WitnessHandshake` in §3.5.2). Nonce from `RandomNumberGenerator.GetBytes`.

**Error handling:** `Encrypt` never fails (any failure inside `AesGcm` throws a `CryptographicException` — but on a 256-bit key derived from the DEK that's a programming-error category, not a runtime fault). `TryDecrypt` returns `false` for wrong DEK, tampered envelope, truncated envelope, unknown version byte; never throws.

### `FlashSkink.Core.Identity.WitnessStore` (internal sealed class)

```csharp
internal sealed class WitnessStore
{
    public WitnessStore(ILogger<WitnessStore> logger);

    internal const string WitnessRemoteName = "_witness/current.enc";

    internal Task<Result<WitnessPayload?>> TryReadAsync(
        IStorageProvider provider,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);

    internal Task<Result> WriteAsync(
        IStorageProvider provider,
        ReadOnlyMemory<byte> dek,
        WitnessPayload payload,
        CancellationToken ct);
}
```

Constructor takes the logger only — DEK and provider are per-call parameters so the same `WitnessStore` instance can serve every tail without holding key material.

---

## Method-body contracts

### `WitnessPayload.ForNewSession(string volumeId, long epoch, string appVersion)`

**Preconditions:** none. (Inputs are not validated — caller is internal.)

**Postconditions:**
- `VolumeId = volumeId`
- `Epoch = epoch`
- `SessionId = Guid.NewGuid().ToString("D")`
- `CommittedAtUtc = DateTime.UtcNow.ToString("O")`
- `Host = Environment.MachineName`
- `AppVersion = appVersion`
- `ConflictObserved = false`

**Never throws** (pure function, Principle 1 carve-out).

### `WitnessPayload.SerializeUtf8`

Returns compact UTF-8 JSON bytes. Field order in the JSON output: `volumeId`, `epoch`, `sessionId`, `committedAtUtc`, `host`, `appVersion`, `conflictObserved` (stable for cosmetic readability of on-disk files).

### `WitnessPayload.TryParse(ReadOnlySpan<byte> utf8, out WitnessPayload payload)`

**Preconditions:** none.

**Behaviour:**
1. If `utf8.IsEmpty` → `false`.
2. Parse JSON; if root is not an object → `false`.
3. For each known field: read if present and correct kind (`Number` for `epoch`, `String` for the others, `True`/`False` for `conflictObserved`); else default (empty string, `0`, or `false`).
4. Construct payload, return `true`.

Catches `JsonException` only; rethrows other exceptions. Per Principle 1, this method is internal and is allowed to surface `JsonException`-derived failures via the `false` return — never throws across the public API. (`WitnessPayload` is `internal`, so "public API" doesn't apply to its own methods, but the discipline is consistent.)

### `WitnessCrypto.Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dek)`

**Preconditions:** `dek.Length == 32` (caller responsibility — internal usage).

**Behaviour:**
1. Allocate `byte[]` of length `1 + 12 + plaintext.Length + 16`.
2. Write version byte at offset 0.
3. `RandomNumberGenerator.GetBytes(envelope.AsSpan(1, 12))` — fills nonce in place.
4. Construct `AesGcm` with the DEK.
5. `aes.Encrypt(nonce, plaintext, ciphertext: envelope.AsSpan(13, plaintext.Length), tag: envelope.AsSpan(13 + plaintext.Length, 16), associatedData: ReadOnlySpan<byte>.Empty)`.
6. Return the envelope.

`AesGcm` is `IDisposable` — wrap in `using` so the underlying key material is zeroed promptly.

### `WitnessCrypto.TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> dek, out byte[] plaintext)`

**Preconditions:** none.

**Behaviour:**
1. If `envelope.Length < 1 + 12 + 16` → `false`, `plaintext = []`.
2. If `envelope[0] != 0x01` → `false`.
3. Slice nonce, tag, ciphertext.
4. `using var aes = new AesGcm(dek, tagSizeInBytes: 16);`
5. Allocate `plaintext = new byte[ciphertextLength]`.
6. `aes.Decrypt(...)` — catch `CryptographicException` → `false`, `plaintext = []`.
7. Return `true`.

### `WitnessStore.TryReadAsync(provider, dek, ct)`

**Preconditions:** `provider` is non-null; `dek.Length == 32`.

**Returns:** `Result<WitnessPayload?>`. Outer `Result.Success` is `true` for all defined situations (we map "tail unreachable" / "witness absent" / "witness corrupted" to `Ok(null)`); outer `Fail` only on `Cancelled` (programming-level cancellation) or `Unknown` (truly unexpected).

**Behaviour:**
1. `ct.ThrowIfCancellationRequested()`.
2. `var listResult = await provider.ListAsync("_witness/", ct).ConfigureAwait(false);`
3. If `!listResult.Success`:
   - If `listResult.Error.Code` is one of `{ ProviderUnreachable, ProviderAuthFailed, TokenExpired, TokenRevoked, TokenRefreshFailed }`: log at Debug, return `Ok(null)` (tail offline; treated as "no information").
   - Otherwise: propagate the error via `Fail(listResult.Error)`.
4. If `listResult.Value.Count == 0`: return `Ok(null)` (no witness on this tail — first use or post-deletion).
5. Take the first remote ID (`listResult.Value[0]`). If `count > 1`, log at Warning ("multiple witness files found; using first") — should not happen in practice but defensive.
6. `var dlResult = await provider.DownloadAsync(remoteId, ct).ConfigureAwait(false);`
7. If `!dlResult.Success`: same offline-tail mapping as step 3.
8. Read all bytes from the returned stream (witness is ~250 bytes; `ReadAllToArray` is fine). `using` the stream.
9. `WitnessCrypto.TryDecrypt(bytes, dek.Span, out var plaintext)`: if `false`, log at Warning, return `Ok(null)` (corrupted witness treated as absent).
10. `WitnessPayload.TryParse(plaintext, out var payload)`: if `false`, log at Warning, return `Ok(null)`.
11. Return `Ok(payload)`.

**Catch ordering:** `OperationCanceledException ex → Result.Fail(Cancelled, ..., ex)`; final `Exception ex → Result.Fail(Unknown, ..., ex)`.

### `WitnessStore.WriteAsync(provider, dek, payload, ct)`

**Preconditions:** same as TryReadAsync.

**Behaviour:**
1. `ct.ThrowIfCancellationRequested()` (only cancellation check point).
2. Delete existing witnesses: `var listResult = await provider.ListAsync("_witness/", CancellationToken.None);` On any failure, log at Debug and continue (best-effort cleanup; the upload below would still overwrite the file contents at the same remote path on FileSystemProvider, though cloud providers may treat overwrite as a new object).

   Actually — to be safer across providers, iterate `listResult.Value` and call `provider.DeleteAsync(id, CancellationToken.None)` for each. On per-id failure, log at Debug, continue.
3. Build envelope: `var envelope = WitnessCrypto.Encrypt(payload.SerializeUtf8(), dek.Span);`
4. `UploadSession? session = null; bool finalised = false;`
5. `var beginResult = await provider.BeginUploadAsync(WitnessRemoteName, envelope.Length, CancellationToken.None);` On failure: log at Warning, return `Fail(StagingFailed, "Failed to begin witness upload on provider '{ProviderId}'", inner: ...)`.
6. `session = beginResult.Value!;`
7. `var rangeResult = await provider.UploadRangeAsync(session, 0, envelope, CancellationToken.None);` On failure: same shape, return `Fail(StagingFailed, ...)`.
8. `var finResult = await provider.FinaliseUploadAsync(session, CancellationToken.None);` On failure: same shape.
9. `finalised = true;`
10. Return `Ok()`.

**Finally block:** if `session != null && !finalised`, `await provider.AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false);` (swallow any failure — best-effort).

**Catch ordering:**
- `OperationCanceledException ex → Result.Fail(Cancelled, "Witness write cancelled at entry.", ex)` (this can ONLY fire from step 1 — every other await uses `CancellationToken.None`).
- final `Exception ex → Result.Fail(Unknown, ..., ex)`.

**Principle 17 compliance:** every await site between step 2 and step 8 uses `CancellationToken.None` spelled out as a literal, not a local. The only `ct` reference is the entry-point `ThrowIfCancellationRequested()`.

---

## Integration points

- **`FlashSkinkVolume.SeedInitialSettingsAsync`** — gains one upsert line. The surrounding transaction logic is unchanged.
- **`FlashSkinkVolume.BackfillAndStampOnOpenAsync`** — return type changes; one call site in `OpenAsync` updates. The new tuple is captured but unused in this PR (it's consumed by §3.5.2's handshake call).
- **`FileSystemProvider.ComputeRemotePath`** — gains a `_witness_` prefix branch. Identical shape to the existing `_brain_` branch.
- **`IStorageProvider`** (the contract) — no changes. The witness write uses the existing upload-session triplet.
- **`UploadQueueService`** — no changes. (Fenced-state enforcement comes in §3.5.2.)

---

## Principles touched

- **Principle 1** (Core never throws across the public API) — `WitnessStore` returns `Result`/`Result<T>`. `WitnessPayload` is `internal`. `VolumeState` and the two new `ErrorCode` values are pure data, no methods that can fail.
- **Principle 13** (`CancellationToken ct` last) — `WitnessStore.TryReadAsync` and `WriteAsync` both end with `CancellationToken ct`.
- **Principle 14** (`OperationCanceledException` first catch) — applies to both async `WitnessStore` methods and to the modified `BackfillAndStampOnOpenAsync`.
- **Principle 15** (no bare `catch (Exception)` as the only catch) — `WitnessStore` catch blocks list `OperationCanceledException` first, `Exception` last.
- **Principle 16** (dispose partially-constructed resources) — `WitnessStore.WriteAsync` puts `AbortUploadAsync` in a `finally` to release the provider-side session if any of the upload-session steps fail.
- **Principle 17** (`CancellationToken.None` literal in compensation paths) — `WitnessStore.WriteAsync` enforces this: every await between the entry-point cancellation check and the final return uses the literal `CancellationToken.None`. Tests verify by counting the calls a `FaultInjectingStorageProvider` receives after triggering the cancellation token mid-write.
- **Principle 20** (`stackalloc` never crosses await) — `WitnessCrypto.Encrypt` uses synchronous `AesGcm`; `stackalloc` is permitted but not used (the envelope is heap-allocated because the caller owns it).
- **Principle 22** (Dapper outside hot paths) — `BackfillAndStampOnOpenAsync` continues to use Dapper. Volume-open scope, not a hot path.
- **Principle 26** (no secrets in logs) — `WitnessStore` logs `ProviderID` and the witness `remoteId`; never the DEK, the envelope bytes, or the plaintext.
- **Principle 28** (Core depends only on MEL abstractions) — `WitnessStore` accepts `ILogger<WitnessStore>`; no Serilog reference.
- **Principle 31** (keys zeroed on volume close) — `WitnessStore` does not hold a long-lived DEK reference. The `ReadOnlyMemory<byte> dek` parameter is consumed synchronously inside each call.

---

## Test spec

### `tests/FlashSkink.Tests/Identity/VolumeStateRoundTripTests.cs` — class `VolumeStateRoundTripTests`

- `Normal_IsZero` — `((int)VolumeState.Normal).Should().Be(0)` (or `Assert.Equal(0, (int)VolumeState.Normal)` per existing convention).
- `Fenced_IsOne`
- `ToString_Normal_ReturnsNormal`
- `ToString_Fenced_ReturnsFenced`
- `Parse_NormalString_ReturnsNormal`
- `Parse_FencedString_ReturnsFenced`
- `Parse_Garbage_ThrowsOrFails` — `Enum.TryParse<VolumeState>("garbage", out _)` returns false. (The test documents the contract that production code MUST use TryParse, not Parse, so corrupted Settings rows don't crash open.)

### `tests/FlashSkink.Tests/Identity/WitnessPayloadTests.cs` — class `WitnessPayloadTests`

- `ForNewSession_SetsAllFields` — factory produces a payload with the provided VolumeId/Epoch/AppVersion, a non-empty SessionId (parseable as Guid), CommittedAtUtc within 1 second of UtcNow, Host equal to MachineName, ConflictObserved=false.
- `SerializeUtf8_ThenTryParse_RoundTripsAllFields`
- `TryParse_Empty_ReturnsFalse`
- `TryParse_NonJson_ReturnsFalse` — `"this is not json"` bytes.
- `TryParse_JsonArrayRoot_ReturnsFalse` — `[]` is valid JSON but wrong root kind.
- `TryParse_MissingConflictObserved_DefaultsFalse` — JSON object without `conflictObserved`; result's `ConflictObserved == false`.
- `TryParse_WithConflictObservedTrue_ParsesCorrectly`
- `TryParse_UnknownFields_AreIgnored` — JSON includes `extraField: "foo"`; parse succeeds; payload values match the known fields.
- `ToDisplayString_ContainsAllFourPublicFields` — assert the rendered string contains the epoch, host, session, and appVersion values.

### `tests/FlashSkink.Tests/Identity/WitnessCryptoTests.cs` — class `WitnessCryptoTests`

- `Encrypt_ThenTryDecrypt_ReturnsOriginalPlaintext` — random 32-byte DEK, random plaintext (use `RandomNumberGenerator.GetBytes(100)`); decrypt result equals input.
- `Encrypt_EmptyPlaintext_RoundTrips` — zero-byte plaintext is supported (degenerate but legal).
- `TryDecrypt_WrongDek_ReturnsFalse` — encrypt with DEK A, decrypt with DEK B; returns false.
- `TryDecrypt_TamperedCiphertext_ReturnsFalse` — flip one bit in the ciphertext region.
- `TryDecrypt_TamperedTag_ReturnsFalse` — flip one bit in the last 16 bytes.
- `TryDecrypt_TruncatedEnvelope_ReturnsFalse` — envelope sliced to length 5.
- `TryDecrypt_WrongVersionByte_ReturnsFalse` — set envelope[0] to 0x02.
- `Encrypt_TwoCallsSamePlaintextSameDek_ProduceDifferentEnvelopes` — nonce randomness check (assert the envelopes differ in the nonce region).

### `tests/FlashSkink.Tests/Identity/WitnessStoreTests.cs` — class `WitnessStoreTests : IDisposable`

Constructor creates a temp dir; `Dispose` deletes it. Builds `FileSystemProvider.Create(...)` rooted at the temp dir (uses the existing `Create` factory's `Result<FileSystemProvider>`). DEK is 32 random bytes generated per-test.

- `TryReadAsync_NoWitnessOnTail_ReturnsOkNull` — fresh temp dir, no witness file; `TryReadAsync` returns `Ok(null)`.
- `WriteAsync_ThenTryReadAsync_ReturnsWrittenPayload` — write, read; payload field-for-field equals.
- `WriteAsync_Twice_SecondWriteReplacesFirst` — write with epoch 1, write with epoch 2; read returns epoch 2.
- `WriteAsync_StoresFileAt_WitnessCurrentEncPath` — after write, assert `File.Exists({tempDir}/_witness/current.enc)`.
- `TryReadAsync_CorruptedFile_ReturnsOkNull` — write a valid witness, then manually `File.WriteAllBytes` the file with garbage; `TryReadAsync` returns `Ok(null)`.
- `TryReadAsync_WrongDek_ReturnsOkNull` — write with DEK A, read with DEK B; returns `Ok(null)` (decrypt fails).
- `WriteAsync_PreCancelledToken_ReturnsCancelled` — pass a cancelled CTS token.
- `TryReadAsync_PreCancelledToken_ReturnsCancelled`
- `TryReadAsync_ProviderUnreachableOnList_ReturnsOkNull` — `FaultInjectingStorageProvider` with `FailNextList(ProviderUnreachable)`.
- `TryReadAsync_ProviderUnreachableOnDownload_ReturnsOkNull` — write a witness, then `FailNextDownload(ProviderUnreachable)`.
- `TryReadAsync_TokenRevokedOnList_ReturnsOkNull` — same shape, different code (broader auth-fail coverage).
- `WriteAsync_AfterFailedBegin_NoSessionLeak` — `FaultInjectingStorageProvider` with `FailNextBegin(ProviderUnreachable)`; `WriteAsync` returns `Fail(StagingFailed)`; assert no orphan staging files in `.flashskink-staging/`.
- `WriteAsync_AfterFailedRange_AbortsSession` — `FailNextRangeWith(UploadFailed)`; assert `WriteAsync` returns `Fail(StagingFailed)`; verify `AbortUploadAsync` was invoked via fault-injector counter (add a `RangeUploadAttempts` / `AbortCount` counter to the injector if not present — verify in implementation; if absent, fall back to checking that no `.partial` / `.session` files remain).

### `tests/FlashSkink.Tests/Identity/VolumeEpochTests.cs` — class `VolumeEpochTests : IDisposable`

Constructor creates a unique skink root in `Path.GetTempPath()`. `Dispose` recursively deletes it. Uses `VolumeCreationOptions` with `NullLoggerFactory.Instance` (the existing test pattern from `VolumeIdentityTests`).

Tests assume the password is a fixed string `"test-password-12345"` (matches the existing pattern in `VolumeIdentityTests`):

- `CreateAsync_SeedsEpochAtOne` — `CreateAsync` → use `OrchestrationTestHelper.ReadSettingAsync` to read `Settings["VolumeEpoch"]` → assert `"1"`.
- `OpenAsync_IncrementsEpoch_FromOneToTwo` — create → dispose → open → read epoch → `"2"`.
- `OpenAsync_IncrementsAgainOnSecondReopen` — create → dispose → open → dispose → open → epoch `"3"`.
- `OpenAsync_LegacyBrainMissingEpochRow_BackfillsToOne` — create → dispose → manually `DeleteSettingAsync(skinkRoot, password, "VolumeEpoch")` → open → epoch `"1"` (treated as missing → 0 → +1 = 1).
- `OpenAsync_GarbageEpochValue_FailsOpen` — manually `UpsertSettingAsync(..., "VolumeEpoch", "not-a-number")` → open fails with `DatabaseReadFailed` (parse failure is a hard error per the dev-plan's defensive-but-not-silent rule). *Decision note: defensive Parse for VolumeState but strict for VolumeEpoch — epoch is the conflict-detection signal and silently treating a corrupted value as 0 would mask the corruption. Trade-off accepted.*
- `BackfillAndStampOnOpenAsync_ReadsVolumeState_AbsentTreatedAsNormal` — create (no VolumeState row seeded) → dispose → open succeeds; assert no Settings["VolumeState"] row exists in the brain (handshake doesn't seed it; that's §3.5.2's job).
- `BackfillAndStampOnOpenAsync_ReadsVolumeState_GarbageTreatedAsNormal` — `UpsertSettingAsync(..., "VolumeState", "not-a-state")` → open succeeds; volume opens without error (defensive parse → Normal).
- `BackfillAndStampOnOpenAsync_ReadsVolumeState_FencedRoundTrips` — `UpsertSettingAsync(..., "VolumeState", "Fenced")` → open succeeds. *Note: this test cannot directly assert that the internal tuple returned Fenced because the §3.5.1 PR doesn't yet expose the value publicly. The test asserts the open succeeds (the row didn't crash anything). The full Fenced→fence-the-volume integration test lands in §3.5.2.*
- `Epoch_PersistsAcrossClose` — write a file (existing WriteFileAsync), close, reopen, verify the file is still readable AND the epoch is `"3"` (one increment from the create+two opens... wait, let me recount). Actually: create=1, dispose, open=2 (write file here), dispose, open=3 (read file here). Sample test for epoch+files cohabiting in the brain without interference.

### `tests/FlashSkink.Tests/Providers/FaultInjectingStorageProvider.cs` modifications

The new knobs follow the existing single-shot pattern:

```csharp
private int _failNextListCount;
private ErrorCode _failNextListCode = ErrorCode.ProviderUnreachable;
private int _failNextDownloadCount;
private ErrorCode _failNextDownloadCode = ErrorCode.ProviderUnreachable;

public void FailNextList() => FailNextListWith(ErrorCode.ProviderUnreachable);
public void FailNextListWith(ErrorCode code) { _failNextListCount++; _failNextListCode = code; }
public void FailNextDownload() => FailNextDownloadWith(ErrorCode.ProviderUnreachable);
public void FailNextDownloadWith(ErrorCode code) { _failNextDownloadCount++; _failNextDownloadCode = code; }

public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
{
    if (_failNextListCount > 0)
    {
        _failNextListCount--;
        return Task.FromResult(Result<IReadOnlyList<string>>.Fail(_failNextListCode, "Fault-injected list failure."));
    }
    return _inner.ListAsync(prefix, ct);
}
// similar for DownloadAsync
```

Each test of the fault path uses one knob call and verifies the single-shot behaviour (a second call to ListAsync after the injected failure works normally).

---

## Acceptance criteria

- [ ] `dotnet build` clean on Windows and Linux with `--warnaserror`.
- [ ] `dotnet test` green: all new tests pass, no existing tests regress.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] Witness file lands at `{rootPath}/_witness/current.enc` for `FileSystemProvider` (verified by `WitnessStore_StoresFileAt_WitnessCurrentEncPath`).
- [ ] `VolumeEpoch` Settings row exists after every `CreateAsync` with value `"1"`.
- [ ] `VolumeEpoch` increments by exactly 1 on every `OpenAsync`.
- [ ] `BackfillAndStampOnOpenAsync` reads `Settings["VolumeState"]` without crashing on absent/corrupt values.
- [ ] `ErrorCode.SplitBrainDetected` and `ErrorCode.VolumeFenced` are visible in `Core.Abstractions`; no existing handler logic touches them yet.
- [ ] `FaultInjectingStorageProvider` exposes `FailNextList` / `FailNextDownload` with single-shot semantics consistent with existing knobs.
- [ ] No new analyzer warnings.
- [ ] Phase-level dev-plan file (`dev-plan/phase-3.5-volume-identity-and-clone-safety.md`) is tracked and committed.

---

## Line-of-code budget

| File | Lines |
|---|---|
| `src/FlashSkink.Core/Identity/VolumeState.cs` | ~30 |
| `src/FlashSkink.Core/Identity/WitnessPayload.cs` | ~110 |
| `src/FlashSkink.Core/Identity/WitnessCrypto.cs` | ~90 |
| `src/FlashSkink.Core/Identity/WitnessStore.cs` | ~180 |
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~40 |
| `src/FlashSkink.Core/Providers/FileSystemProvider.cs` (delta) | ~10 |
| `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` (delta) | ~15 |
| **Total src delta** | **~475** |
| `tests/.../Identity/VolumeStateRoundTripTests.cs` | ~60 |
| `tests/.../Identity/WitnessPayloadTests.cs` | ~110 |
| `tests/.../Identity/WitnessCryptoTests.cs` | ~120 |
| `tests/.../Identity/WitnessStoreTests.cs` | ~180 |
| `tests/.../Identity/VolumeEpochTests.cs` | ~140 |
| `tests/.../Providers/FaultInjectingStorageProvider.cs` (delta) | ~35 |
| **Total test delta** | **~645** |

The dev-plan-level budget for §3.5.1 was ~360 src / ~410 test. This PR plan is higher (~475 src / ~645 test). Two reasons: (a) the dev-plan estimate didn't account for the `FileSystemProvider` `_witness_` prefix patch or the `FaultInjectingStorageProvider` knobs; (b) test coverage is broader than the dev-plan's headline-test list (more edge cases per file). Both are intentional — the additional test mass is for `WitnessCrypto` envelope edge cases that surface real bugs if missing.

---

## Non-goals

- Do NOT perform any witness comparison or split-brain detection — that is §3.5.2's `WitnessHandshake.RunAsync`.
- Do NOT modify `UploadQueueService` — the fenced-state guard lands in §3.5.2.
- Do NOT add `FlashSkinkVolume.PromoteAsync` — §3.5.3.
- Do NOT add `FlashSkinkVolume.State` property — §3.5.2 (the handshake is what produces a value worth exposing).
- Do NOT call `BackfillAndStampOnOpenAsync` from `CreateAsync` — the seed-`"1"` decision documented in Scope avoids needing this.
- Do NOT add the `_witness_` prefix to BrainMirrorService, AuditService, or any other consumer — `WitnessStore` is the only writer/reader of the witness path.
- Do NOT add a `BackgroundFailures` row for any witness-related failure in this PR — `WitnessStore` log-then-return-Ok-null on read failures; write failures return `Fail(StagingFailed)` without `BackgroundFailures` (the §3.5.2 handshake handles `BackgroundFailures` writes for true conflict scenarios).
- Do NOT update `BLUEPRINT.md` — Phase 3.5 blueprint sections (§19.6, §19.7, §19.8) all land in §3.5.2 and §3.5.3, NOT here. CLAUDE.md is also untouched (Principle 35 is from Refactor PR B; no new principles in §3.5.1).
- Do NOT mark `WitnessStore` as `public` — it is an internal-only type. If §3.5.2 needs cross-assembly access in tests, prefer `InternalsVisibleTo` over a public surface.
