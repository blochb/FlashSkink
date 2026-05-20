# Phase 3.5 — Volume identity and clone safety

**Status marker:** This phase follows the standard session protocol defined in `CLAUDE.md`. Each section below (§3.5.1 through §3.5.3) maps to one PR, executed via `read section 3.5.X of the dev plan and perform`. Gate 1 (plan approval) and Gate 2 (implementation approval) are required for every section. Sections must be executed in order.

**Sequencing:** Phase 3.5 lands after §3.6 (merged), Refactor PR A (merged, PR #69), and Refactor PR B (merged, PR #72). Phase 4 (CLI and cloud providers) begins after §3.5.3 merges.

**Meta-plan reference:** Items 10–14 of `.claude/plans/agreed-clone-deferred-now-bright-popcorn.md`.

---

## Goal

After Phase 3.5:

- Every volume open performs a **witness handshake**: the volume reads a small encrypted file (`_witness/current.enc`) from each accessible tail, compares the tail's recorded epoch to the local brain's `VolumeEpoch`, and detects whether a diverged clone has performed sessions that the local brain does not know about.
- A detected split-brain puts the volume into **fenced state**: Phase 1 writes (skink) and reads continue normally; Phase 2 uploads to all tails are blocked. The fenced state is persisted in `Settings["VolumeState"]` so it survives close/reopen.
- **Two-sided fencing**: when the higher-epoch holder detects a conflict, it writes `"ConflictObserved": true` into its new witness. The lower-epoch holder reads this marker on its next session open and also enters fenced state — ensuring both parties see the conflict even if they never overlap in time.
- A `Critical` notification and a `BackgroundFailures` row are written so the user cannot miss the event.
- **`FlashSkinkVolume.PromoteAsync`** lets the user explicitly choose which skink is canonical: it exits fenced state, clears the conflict marker, writes a fresh witness to all accessible tails, and resumes Phase 2 uploads.
- A new blueprint section documents the full witness protocol, fenced-state semantics, and the `promote` resolution flow.
- **Phase 4 can start** with clone-safety in place — every subsequent write session advances the epoch and writes an updated witness, keeping the conflict-detection contract maintained from first use.

---

## Cross-cutting decisions

**1. Witness encryption — AES-256-GCM with the DEK, simple binary envelope.**
The witness file is small infrastructure metadata (< 512 bytes plaintext). Using the full `CryptoPipeline` blob format (compression, per-blob header, incremental hash) would be wasteful and would create an undesirable dependency from the `Identity/` layer into the pipeline layer. Instead, `WitnessStore` uses `System.Security.Cryptography.AesGcm` directly with the DEK, producing a minimal binary envelope:

```
[1 byte : version = 0x01]
[12 bytes: random nonce (RandomNumberGenerator.GetBytes)]
[N bytes : AES-256-GCM ciphertext of the UTF-8 JSON payload]
[16 bytes: AES-256-GCM authentication tag]
```

The version byte reserves room for a future re-key or format change; current readers reject anything other than `0x01` as `WitnessParseResult.UnsupportedVersion`. The DEK is passed in as `ReadOnlyMemory<byte>` at each call site — `WitnessStore` never holds a reference to key material (Principle 31 applies to all key-adjacent types: no long-lived key references).

**2. VolumeEpoch — long stored as decimal string in `Settings["VolumeEpoch"]`.**
The epoch starts at `0` after `SeedInitialSettingsAsync`. Each call to `OpenAsync` (and `CreateAsync`) increments it atomically in the same brain transaction as `AppVersionLastOpened`/`AppVersionLastOpenedUtc`. The transaction ensures the epoch write is durable before any upload or mirror activity begins for that session. There is no per-file or per-upload epoch granularity — one increment per volume open is the unit of comparison. A monotonically increasing counter is the conflict signal; wall-clock timestamps are informational only (clock skew cannot cause false positives or false negatives).

**3. Witness remote path — `_witness/current.enc`.**
The `_` prefix groups infrastructure objects with `_brain/` mirrors. `WitnessStore.WriteAsync` passes `"_witness/current.enc"` as the `remoteName` to `BeginUploadAsync`; to read, it calls `provider.ListAsync("_witness/")` to obtain the provider-assigned `remoteId`, then `DownloadAsync(remoteId)`. Before writing, it deletes any existing `_witness/` objects so there is always at most one witness per tail. The write uses `CancellationToken.None` at every await site (Principle 17): once the handshake decides to commit the new epoch, the witness write must complete.

**4. Tail availability is not a prerequisite for a clean open.**
A tail that is offline, returns `ProviderUnreachable`, or fails OAuth during the handshake is skipped (logged at `Debug`; the open continues). Split-brain is only detected when a reachable tail returns a witness with `Epoch > localEpoch`. If zero tails are reachable, the handshake succeeds silently — no new information is available. An offline tail can never confirm OR deny a split-brain, so optimistic silence is the safe default (the user did not split-brain; the network is simply down). This mirrors the `UploadQueueService`'s existing posture toward unreachable tails.

**5. Fenced state is persisted in `Settings["VolumeState"]`.**
Values are `"Normal"` (default; absent key is equivalent) and `"Fenced"`. `BackfillAndStampOnOpenAsync` (already called on every open in `FlashSkinkVolume.OpenAsync`) is extended to read and return this value. `UploadQueueService` receives the initial fenced state at construction via a new `bool initiallyFenced` constructor parameter. `WitnessHandshake` exposes a `SetFenced(bool)` callback (injected by `FlashSkinkVolume`) that simultaneously writes `Settings["VolumeState"]` and calls `uploadQueueService.SetFenced(bool)`. The `UploadQueueService` holds a `volatile bool _fenced`; the orchestrator and every worker check it at the top of each tick and idle when true (no brain reads in the hot loop — Principle 22).

**6. New `ErrorCode` values in this phase — `SplitBrainDetected`, `VolumeFenced`.**
`SplitBrainDetected` is returned by `WitnessHandshake.RunAsync` when a reachable tail's witness shows `Epoch > localEpoch`. Metadata keys: `TailProviderID`, `WitnessEpoch`, `LocalEpoch`, `WitnessHost`, `WitnessSessionId`, `ConflictObserved`. `VolumeFenced` is returned by any Phase 2 upload attempt when `_fenced == true`; the upload is skipped and the caller observes `ErrorCode.VolumeFenced`. Neither code is used by any existing PR; no renumbering required.

**7. `PromoteAsync` is unconditional — it does not verify tail epoch.**
The user has already decided which skink wins. `PromoteAsync` writes a fresh witness (with `ConflictObserved = false`) to every accessible tail, clears `Settings["VolumeState"]`, calls `uploadQueueService.SetFenced(false)`, and wakes the upload signal. If tails are offline at promote time, the fresh witness is written when those tails next become reachable — but the protocol does NOT require `PromoteAsync` itself to wait for offline tails: the **next session open's handshake** will overwrite any stale `ConflictObserved=true` witness on a previously-offline tail with a fresh one (because `alreadyFenced=false` after a successful promote). This means `PromoteAsync` can be idempotent and offline-tolerant without any per-tail repair queue. The demoted skink's witness, which had `ConflictObserved = true`, will be overwritten the next time the winner opens the volume — at which point the demoted skink (on its own next open) sees the fresh `ConflictObserved = false` witness with an epoch ahead of its own and is directed to either re-promote (overriding the previous decision) or recover via a tail (the canonical exit path for the demoted skink).

**8. Provider registry must be shared across open/close cycles in Phase 3.5 tests.**
`BuildVolumeFromSessionAsync` constructs a fresh `InMemoryProviderRegistry` when `options.ProviderRegistry` is null. Without action, every `OpenAsync` would therefore see an empty registry, and the witness handshake would never run against any tail in the current Phase 3 lifecycle. Phase 3.5 integration tests (§3.5.2 and §3.5.3) MUST construct one `InMemoryProviderRegistry` per test, register the test tails on it once, and pass it via `VolumeCreationOptions.ProviderRegistry` to every `CreateAsync`/`OpenAsync` call in the test. The registry's contents persist because it is owned by the test, not by the volume. Production Phase 4 ships `BrainBackedProviderRegistry`, which reads `Providers` rows on every open and constructs adapters from decrypted refresh tokens — eliminating this concern entirely outside tests. The plan does not introduce `BrainBackedProviderRegistry` in Phase 3.5; the test pattern is the bridge.

**9. The witness handshake adds one provider round-trip per tail per session open.**
A handshake against N tails performs at most: N × `ListAsync("_witness/")`, plus up to N × `DownloadAsync` (when a witness exists), plus N × the upload-session triplet (`BeginUploadAsync`/`UploadRangeAsync`/`FinaliseUploadAsync`) when writing the new witness. For `FileSystemProvider` this is sub-millisecond per tail; for cloud providers (Phase 4) this is one extra HTTPS round-trip per tail at session open. The cost is paid once per session open, never per file write. Recording the cost here so Phase 4 sizing isn't surprised.

**10. The upload-session protocol is reused for witness writes despite the size mismatch.**
A witness is ~200 bytes plaintext, ~250 bytes encrypted. The session protocol (begin/range/finalise/atomic-rename/fsync) is ~5× the I/O overhead of a direct small-file write. The trade-off is accepted because (a) the session protocol already guarantees crash-safety — a half-written witness cannot be observed by a peer because finalise is atomic; (b) adding a "write small file" primitive to `IStorageProvider` would expand a contract that Principle 23 freezes at V1; (c) the cost is one-per-open, dwarfed by network latency on cloud providers and noise on local providers.

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §3.5.1 | Witness foundation | `WitnessPayload`, `WitnessCrypto`, `WitnessStore`, `VolumeState` enum, `VolumeEpoch` increment + `VolumeState` read in `SeedInitialSettingsAsync` + `BackfillAndStampOnOpenAsync` (return type widens to `(VolumeState, long)` in one step), new `ErrorCode` values, tests |
| §3.5.2 | Session-begin handshake and fenced state | `WitnessHandshake`, fenced state wired into `FlashSkinkVolume.OpenAsync`, `UploadQueueService.SetFenced`, `RegisterTailAsync` writes initial witness, `BackgroundFailures` write, two-sided fence, auto-downgrade on stale `Fenced` brain row, Blueprint §19.6–19.7, tests |
| §3.5.3 | Resolution flow and edge-case hardening | `FlashSkinkVolume.PromoteAsync`, edge-case hardening, Blueprint §19.8, tests |

Full implementation detail for each section lives in `.claude/plans/pr-3.5.X.md`, written at Gate 1 of the corresponding session.

---

## Section notes

### §3.5.1 — Witness foundation

**Blueprint sections to read:** §19.5 (single-instance lock, for the two-file separation pattern), §31.1 (VolumeID — the field that populates `WitnessPayload.VolumeId`), §31.2–31.3 (app version, for `WitnessPayload.AppVersion`), §18.6 (DEK lifecycle — confirms the DEK is available through the session).

**Scope summary:** New `src/FlashSkink.Core/Identity/` folder with four files (three types + the `VolumeState` enum, moved here from §3.5.2 so `BackfillAndStampOnOpenAsync`'s return type lands in its final shape on the first change); two modifications to `FlashSkinkVolume.cs`; one modification to `ErrorCode.cs`. No blueprint changes yet (those land in §3.5.2). No new NuGet packages — `System.Security.Cryptography.AesGcm` is in the .NET 10 BCL.

---

#### Files to create

**`src/FlashSkink.Core/Identity/VolumeState.cs`** (~25 lines)

```
namespace FlashSkink.Core.Identity
```

`public enum VolumeState` with two values:
- `Normal = 0` — default; no conflict detected; uploads proceed.
- `Fenced = 1` — split-brain detected in a prior session-begin handshake; Phase 2 uploads blocked; resolved by `FlashSkinkVolume.PromoteAsync` (added in §3.5.3).

The enum lives here in §3.5.1 even though the *enforcement* logic (handshake, fenced guard, promote) lands in §3.5.2 and §3.5.3. Reason: `BackfillAndStampOnOpenAsync` reads `Settings["VolumeState"]` and its return type widens to `Task<Result<(VolumeState State, long NewEpoch)>>` in this PR. Defining the enum here lets the return type land in its final shape on the first change rather than churning twice.

XML doc comment on the type cross-references `FlashSkink.Core.Orchestration.FlashSkinkVolume.PromoteAsync` and Blueprint §19.7 even though both arrive later — the forward references are intentional placeholders so the reader sees the full story from the type definition.

---

**`src/FlashSkink.Core/Identity/WitnessPayload.cs`** (~80 lines)

```
namespace FlashSkink.Core.Identity
```

`internal readonly record struct WitnessPayload` with fields:
- `string VolumeId` — must match `Settings["VolumeID"]`; used as additional authenticated data (AAD) in AES-256-GCM to bind the witness cryptographically to the volume identity
- `long Epoch` — the `Settings["VolumeEpoch"]` value at the time this witness was written
- `string SessionId` — `Guid.NewGuid().ToString("D")` per session open; disambiguates concurrent writes from the same volume (rare but possible with the TOCTOU window in force-unlock)
- `string CommittedAtUtc` — `DateTime.UtcNow.ToString("O")`; informational only, never used in conflict logic
- `string Host` — `Environment.MachineName`; surfaced in user-facing error messages
- `string AppVersion` — `GetAppInformationalVersion()`
- `bool ConflictObserved` — `false` by default; set to `true` by the higher-epoch holder when it detects a split-brain (two-sided fence, §3.5.2)

Methods:
- `internal static WitnessPayload ForNewSession(string volumeId, long epoch, string appVersion)` — factory; sets `SessionId = Guid.NewGuid().ToString("D")`, `CommittedAtUtc = DateTime.UtcNow.ToString("O")`, `Host = Environment.MachineName`, `ConflictObserved = false`. Never throws (pure function, Principle 1 exception).
- `internal byte[] SerializeUtf8()` — compact JSON via `System.Text.Json.JsonSerializer.SerializeToUtf8Bytes`. Field order fixed for cosmetic stability; readers tolerate any order.
- `internal static bool TryParse(ReadOnlySpan<byte> utf8, out WitnessPayload payload)` — forgiving; returns `false` for empty, non-JSON, or object-root-mismatch. Unknown fields are silently ignored. Missing `ConflictObserved` defaults to `false` (additive field, backward-compatible).
- `internal string ToDisplayString()` — single-line human summary for `ErrorContext.Message`; format: `"epoch={Epoch}, host={Host}, session={SessionId}, appVersion={AppVersion}"`.

JSON field names (camelCase, matching `InstanceLockManifest` style): `volumeId`, `epoch`, `sessionId`, `committedAtUtc`, `host`, `appVersion`, `conflictObserved`.

---

**`src/FlashSkink.Core/Identity/WitnessCrypto.cs`** (~80 lines)

```
namespace FlashSkink.Core.Identity
```

`internal static class WitnessCrypto`

Static methods only. `AesGcm` is instantiated fresh per call (not pooled) because the witness file is tiny and written once per session open — the allocation cost is negligible.

- `internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dek)` — generates a 12-byte nonce via `RandomNumberGenerator.GetBytes`, encrypts with `AesGcm`, returns the binary envelope: `[0x01][nonce (12)][ciphertext (N)][tag (16)]`. The AAD passed to `AesGcm.Encrypt` is the empty span (the binding to `VolumeId` is inside the plaintext; a separate AAD binding was considered but rejected as added complexity for no correctness gain — the ciphertext authenticity already proves the DEK holder wrote this file).
- `internal static bool TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> dek, out byte[] plaintext)` — parses the version byte (returns `false` for any value ≠ `0x01`), extracts nonce and tag, decrypts. Returns `false` on any `CryptographicException` (wrong key, tampered data, truncated file). Never throws.

Note: `stackalloc` is acceptable for the nonce and tag spans inside these methods since they are synchronous and do not cross any `await` boundary (Principle 20).

---

**`src/FlashSkink.Core/Identity/WitnessStore.cs`** (~130 lines)

```
namespace FlashSkink.Core.Identity
```

`internal sealed class WitnessStore`

Constructor: `WitnessStore(ILogger<WitnessStore> logger)`. No stateful fields beyond the logger — all DEK and provider inputs are per-call parameters.

Methods:

- `internal async Task<Result<WitnessPayload?>> TryReadAsync(IStorageProvider provider, ReadOnlyMemory<byte> dek, CancellationToken ct)` — reads the current witness from the given tail:
  1. `provider.ListAsync("_witness/", ct)` → on `ProviderUnreachable`/`ProviderAuthFailed`/`TokenExpired`/`TokenRevoked`/`TokenRefreshFailed` return `Result<WitnessPayload?>.Ok(null)` (tail offline, not an error). On other failures propagate.
  2. If the list is empty → return `Result<WitnessPayload?>.Ok(null)` (witness absent, first use).
  3. `provider.DownloadAsync(remoteIds[0], ct)` to obtain the stream.
  4. Read all bytes from the stream (the witness is always < 1 KB; `MemoryStream` or `IMemoryOwner` acceptable, but allocate a fresh `byte[]` from the stream length — not a hot path).
  5. `WitnessCrypto.TryDecrypt(bytes, dek.Span, out var plaintext)` → if false, log `Warning` and return `Result<WitnessPayload?>.Ok(null)` (treat as absent; corrupted/partial witness is not a split-brain signal, it is treated as "no information").
  6. `WitnessPayload.TryParse(plaintext, out var payload)` → if false, same treatment as step 5.
  7. Return `Result<WitnessPayload?>.Ok(payload)`.
  - Catch ordering: `OperationCanceledException` first → `Cancelled`; then `Exception` → `Unknown`.

- `internal async Task<Result> WriteAsync(IStorageProvider provider, ReadOnlyMemory<byte> dek, WitnessPayload payload, CancellationToken ct)` — writes a fresh witness to the tail, replacing any existing one:
  1. `provider.ListAsync("_witness/", CancellationToken.None)` → delete each existing remoteId via `provider.DeleteAsync(id, CancellationToken.None)`. Errors on delete are logged at `Debug` and ignored (best-effort cleanup; the upload in step 2 will overwrite the data regardless).
  2. Encrypt: `WitnessCrypto.Encrypt(payload.SerializeUtf8(), dek.Span)`.
  3. Begin upload: `provider.BeginUploadAsync("_witness/current.enc", encryptedBytes.Length, CancellationToken.None)`.
  4. Upload single range: `provider.UploadRangeAsync(session, 0, encryptedBytes, CancellationToken.None)`.
  5. Finalise: `provider.FinaliseUploadAsync(session, CancellationToken.None)`.
  - All awaits in steps 1–5 use `CancellationToken.None` (Principle 17 — the witness write is the atomic-commit step of the session-begin handshake; abandoning it mid-write leaves the tail in an unknown state with no cleanup owner).
  - `ct` is observed via `ct.ThrowIfCancellationRequested()` at method entry only.
  - On any `IStorageProvider` failure in steps 3–5, call `provider.AbortUploadAsync(session, CancellationToken.None)` in a `finally` block (best-effort; mirrors the pattern in `RangeUploader`).
  - Catch ordering: `OperationCanceledException` → `Cancelled`; `Exception` → map to `StagingFailed` with the tail's `ProviderID` in the message.

---

#### Files to modify

**`src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs`**

Two changes, both in private static helpers:

1. `SeedInitialSettingsAsync` — add one `upsert` statement for `VolumeEpoch` with value `"0"`, inside the existing transaction. Insert position: after the `VolumeID` row, before `AppVersionCreatedWith`. No other changes to this method.

2. `BackfillAndStampOnOpenAsync` — return type changes from `Task<Result>` (no value) to **`Task<Result<(VolumeState State, long NewEpoch)>>`** in one step. Inside the same transaction it already uses:
   - Read `Settings["VolumeState"]`. Treat absent row as `VolumeState.Normal`. Parse via `Enum.TryParse<VolumeState>(value, out var state)`; treat unparseable values as `Normal` (defensive — a corrupted Settings row should not block opening).
   - Read `Settings["VolumeEpoch"]`. Parse as `long`; if absent (legacy brain), treat as `0`.
   - Compute `newEpoch = currentEpoch + 1`.
   - Upsert: `INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('VolumeEpoch', @NewEpoch)`.
   - The fenced-state row is NOT written here (only read) — the handshake in §3.5.2 owns writes to `Settings["VolumeState"]`.
   - Return `Result<(VolumeState, long)>.Ok((state, newEpoch))`.

   Update the call site in `OpenAsync` to receive the tuple and stash both values for the §3.5.2 handshake call to use.

   **`SeedInitialSettingsAsync`** does NOT seed `VolumeState` — its absence at first open is correctly interpreted as `Normal` by the read above, and avoiding the seed keeps the §3.5.1 diff localised to one new row (`VolumeEpoch`). The first `BackfillAndStampOnOpenAsync` call after `SeedInitialSettingsAsync` increments seed-epoch `0` to `1` and reads `VolumeState` as `Normal`. The first session ever is epoch 1, state Normal.

**`src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs`**

Add two new values in a new `// ── Clone safety ──` block after the `// ── Healing ──` block:

```csharp
// ── Clone safety ────────────────────────────────────────────────────────────

/// <summary>
/// A tail's witness epoch is higher than the local brain's epoch, indicating
/// a diverged clone has performed sessions that this volume does not contain.
/// The volume transitions to fenced state.
/// </summary>
SplitBrainDetected,

/// <summary>
/// The volume is in fenced state (split-brain was detected in a prior session).
/// Phase 2 uploads are blocked until the user resolves the conflict via
/// <see cref="FlashSkink.Core.Orchestration.FlashSkinkVolume.PromoteAsync"/>.
/// </summary>
VolumeFenced,
```

---

#### Test spec

**`tests/FlashSkink.Tests/Identity/WitnessPayloadTests.cs`** — class `WitnessPayloadTests`

- `SerializeRoundTrip_PreservesAllFields` — `ForNewSession` → `SerializeUtf8` → `TryParse` → assert all fields equal.
- `TryParse_Empty_ReturnsFalse`
- `TryParse_NonJson_ReturnsFalse`
- `TryParse_MissingConflictObserved_DefaultsFalse` — JSON without `conflictObserved` key; `payload.ConflictObserved` is `false`.
- `TryParse_WithConflictObservedTrue_ParsesCorrectly`
- `ToDisplayString_ContainsEpochHostVersion`

**`tests/FlashSkink.Tests/Identity/WitnessCryptoTests.cs`** — class `WitnessCryptoTests`

- `Encrypt_ThenDecrypt_ReturnsOriginalPlaintext`
- `TryDecrypt_WrongDek_ReturnsFalse`
- `TryDecrypt_TamperedCiphertext_ReturnsFalse`
- `TryDecrypt_TruncatedEnvelope_ReturnsFalse`
- `TryDecrypt_WrongVersionByte_ReturnsFalse` — first byte set to `0x02`; returns false.
- `Encrypt_TwoCallsSamePlaintext_ProduceDifferentCiphertexts` — nonce is random; two encryptions of identical plaintext are not identical.

**`tests/FlashSkink.Tests/Identity/WitnessStoreTests.cs`** — class `WitnessStoreTests`

Uses `FileSystemProvider` rooted at a per-test temp dir. Uses real DEK bytes (`RandomNumberGenerator.GetBytes(32)`).

- `TryReadAsync_WitnessAbsent_ReturnsNullPayload`
- `WriteAsync_ThenTryReadAsync_ReturnsWrittenPayload`
- `WriteAsync_Twice_ReplacesExistingWitness` — after two writes with different epochs, `TryReadAsync` returns the second epoch.
- `TryReadAsync_CorruptedWitnessFile_ReturnsNullPayload` — manually corrupts the `.enc` file after writing; `TryReadAsync` returns `(success: true, value: null)`.
- `TryReadAsync_WrongDek_ReturnsNullPayload`
- `WriteAsync_CancelledBeforeStart_ReturnsCancelled` — pre-cancelled `CancellationToken`; `ct.ThrowIfCancellationRequested()` fires before provider calls.
- `TryReadAsync_ProviderUnreachable_ReturnsNullPayload` — wraps `FileSystemProvider` with `FaultInjectingStorageProvider` configured to return `ProviderUnreachable` on `ListAsync`; returns `(success: true, value: null)`.

**`tests/FlashSkink.Tests/Identity/VolumeEpochTests.cs`** — class `VolumeEpochTests`

Integration tests via `FlashSkinkVolume` (use the existing `VolumeTestHarness` pattern from Orchestration tests).

- `Epoch_StartsAtOne_AfterCreate` — `CreateAsync` → read `Settings["VolumeEpoch"]` via a direct `SqliteConnection`; assert value is `"1"`.
- `Epoch_IncrementsOnEachOpen` — create → dispose → open → assert epoch `"2"` → dispose → open → assert `"3"`.
- `Epoch_BackfilledToOne_OnLegacyBrainMissingRow` — manually delete the `VolumeEpoch` Settings row, close and reopen; assert epoch is `"1"` after reopen.
- `VolumeState_AbsentRow_TreatedAsNormal` — open a freshly-created volume (no `VolumeState` row was seeded); `BackfillAndStampOnOpenAsync` returns `(VolumeState.Normal, …)`.
- `VolumeState_CorruptedValue_TreatedAsNormal` — manually write `Settings["VolumeState"] = "garbage"`; reopen; returns `Normal` (defensive parse, does not block open).
- `VolumeState_FencedValue_RoundTrips` — manually write `Settings["VolumeState"] = "Fenced"`; reopen; returns `Fenced`.

---

#### Line-of-code budget

| File | Lines |
|---|---|
| `src/FlashSkink.Core/Identity/VolumeState.cs` | ~25 |
| `src/FlashSkink.Core/Identity/WitnessPayload.cs` | ~80 |
| `src/FlashSkink.Core/Identity/WitnessCrypto.cs` | ~80 |
| `src/FlashSkink.Core/Identity/WitnessStore.cs` | ~130 |
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~30 |
| `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` (delta) | ~15 |
| **Total src delta** | **~360** |
| `tests/.../Identity/WitnessPayloadTests.cs` | ~80 |
| `tests/.../Identity/WitnessCryptoTests.cs` | ~100 |
| `tests/.../Identity/WitnessStoreTests.cs` | ~120 |
| `tests/.../Identity/VolumeEpochTests.cs` | ~110 |
| **Total test delta** | **~410** |

---

#### Principles touched

- **P1** (Core never throws across public API) — `WitnessStore`, `WitnessHandshake`-callable surface, and the modified `BackfillAndStampOnOpenAsync` all return `Result`/`Result<T>`.
- **P13** (`CancellationToken ct` last) — all new async methods accept `ct` as the final parameter.
- **P14** (`OperationCanceledException` first catch) — `WitnessStore.TryReadAsync`/`WriteAsync` and the `BackfillAndStampOnOpenAsync` delta catch ordering is `OCE → SqliteException → Exception`.
- **P15** (no bare `catch (Exception)` as sole catch) — every catch block in new code has at least one specific type before the fallback.
- **P16** (dispose partially-constructed resources) — `WitnessStore.WriteAsync` puts `AbortUploadAsync` in a `finally` to release the provider-side session on mid-upload failure.
- **P17** (`CancellationToken.None` literal in compensation) — `WitnessStore.WriteAsync`'s session-begin / range / finalise calls pass `CancellationToken.None` spelled out at every await site; the entry-point `ct.ThrowIfCancellationRequested()` is the only cancellation check.
- **P20** (`stackalloc` does not cross await) — `WitnessCrypto.Encrypt` may use `stackalloc Span<byte>` for the 12-byte nonce; all GCM operations are synchronous.
- **P22** (Dapper acceptable outside hot paths) — `BackfillAndStampOnOpenAsync` continues to use Dapper for Settings reads/writes; this is volume-open scope, not a per-byte hot path.
- **P26** (no secrets in logs) — `WitnessStore` logs `ProviderID` and remote IDs but never the DEK, the witness ciphertext, or the witness plaintext.
- **P28** (Core depends only on MEL abstractions) — `WitnessStore` accepts `ILogger<WitnessStore>` from MEL.Abstractions only.

#### Non-goals for §3.5.1

- Do NOT perform any witness comparison or split-brain detection (§3.5.2).
- Do NOT enforce the fenced state in `UploadQueueService` — `VolumeState` is defined here but its semantic enforcement lives in §3.5.2.
- Do NOT add `PromoteAsync` (§3.5.3).
- Do NOT modify `UploadQueueService` (§3.5.2).
- Do NOT add BLUEPRINT changes (§3.5.2 and §3.5.3).
- Do NOT seed `Settings["VolumeState"]` in `SeedInitialSettingsAsync` — absent row is correctly interpreted as `Normal`.

---

### §3.5.2 — Session-begin handshake and fenced state

**Blueprint sections to read:** §8.5–8.6 (BackgroundFailures write on Critical events, Principle 24), §19.5 (single-instance lock — the witness handshake fires in the same window, after lock acquisition), §31.1 (VolumeID — used to validate the witness against the volume we think we opened).

**Scope summary:** New `WitnessHandshake` type in `Identity/`; three modifications in `FlashSkinkVolume.cs` (handshake wiring, `RegisterTailAsync` writes an initial witness on tail registration, `State` property exposed); one modification to `UploadQueueService` (fenced guard); BLUEPRINT §19.6–19.7; tests. `VolumeState` already exists from §3.5.1; this section consumes it.

---

#### Files to create

**`src/FlashSkink.Core/Identity/WitnessHandshake.cs`** (~200 lines)

```
namespace FlashSkink.Core.Identity
```

`internal sealed class WitnessHandshake`

Constructor: `WitnessHandshake(WitnessStore store, ILogger<WitnessHandshake> logger)`.

Primary method:

```csharp
internal async Task<Result<WitnessHandshakeOutcome>> RunAsync(
    IReadOnlyList<(string ProviderId, IStorageProvider Provider)> tails,
    string volumeId,
    long newEpoch,
    ReadOnlyMemory<byte> dek,
    bool alreadyFenced,
    CancellationToken ct)
```

`WitnessHandshakeOutcome` is a `readonly record struct`:
```csharp
internal enum ConflictTrigger
{
    None = 0,
    EpochComparison = 1,   // a tail's witness epoch >= our new epoch
    ConflictMarker = 2,    // a tail's witness ConflictObserved=true (lagger learning the winner saw us)
}

internal readonly record struct WitnessHandshakeOutcome(
    bool ConflictDetected,
    ConflictTrigger Trigger,
    string? ConflictingProviderId,   // first tail that triggered the conflict
    WitnessPayload? ConflictWitness, // the offending witness payload (for metadata)
    int TailsRead,                   // number of tails where ListAsync succeeded
    int TailsWritten)                // number of tails where WriteAsync succeeded
```

Algorithm:

1. `ct.ThrowIfCancellationRequested()`.
2. For each tail in `tails` (sequential — no parallel provider I/O in the handshake; sequencing the writes through one tail at a time keeps the "winner writes to all tails" step from racing itself):
   a. `WitnessStore.TryReadAsync(provider, dek, ct)`. If the read fails (returns non-null error, not a null payload), log at `Debug` and skip this tail.
   b. If `payload` is null: this tail has no witness — treat as "no information"; proceed (no conflict signal from this tail).
   c. Validate `payload.VolumeId == volumeId`. If mismatch: log at `Warning` (this should not happen in normal use; the path is volume-scoped and the ciphertext is bound to the DEK); skip this tail. (Documented behaviour: see Blueprint §19.6 — a mismatched-VolumeId witness is treated as "no information from this tail", same as a corrupted or absent witness.)
   d. **Conflict detection — two parallel triggers, BOTH evaluated:**
      - **Trigger A (epoch comparison):** if `payload.Epoch >= newEpoch`, set `conflictDetected = true`, `Trigger = EpochComparison`, record `ConflictingProviderId` and `ConflictWitness`. Rationale: another skink has advanced the epoch to a value at or beyond our new epoch, meaning either it ran sessions after our common ancestor (`payload.Epoch > newEpoch`), or both skinks just advanced from the same `oldEpoch` independently (`payload.Epoch == newEpoch` — collision case).
      - **Trigger B (conflict marker):** if `payload.Epoch < newEpoch` AND `payload.ConflictObserved == true`, set `conflictDetected = true`, `Trigger = ConflictMarker`, record `ConflictingProviderId` and `ConflictWitness`. Rationale: the OTHER skink — the winner that wrote this witness — detected a conflict against us in a prior session and stamped the marker so we (the lagger) learn about it even though our epoch is now ahead. This is the lagger-detection half of the two-sided fence (meta-plan item 12).
      - If neither trigger fires, this tail is fine; continue to the next tail.
      - The two triggers are NOT short-circuited per tail — both are evaluated against each tail, but if *any* tail triggers ANY conflict, the volume is fenced. First-trigger-wins for the `ConflictingProviderId` and `ConflictWitness` fields (deterministic given the input tail order).
3. **If `conflictDetected || alreadyFenced`** (we are entering or remaining in the fenced state):
   - Build the new witness payload with `ConflictObserved = true` — we write it so the OTHER skink sees it on its next open and also enters fenced state, even if its own epoch is lower than ours.
   - Write the witness (with `ConflictObserved = true`) to ALL accessible tails (not just the conflicting one). This propagates the marker across the full tail set so the other skink's next handshake will pick it up from any tail it can reach.
   - Return `WitnessHandshakeOutcome { ConflictDetected = true, Trigger = …, … }` (use the first triggering tail's data; if `alreadyFenced` and no fresh conflict was detected, `Trigger = None` but `ConflictDetected = true` because the alreadyFenced state forced the fence-perpetuating write — note for the caller: this case is handled below).

   Actually, to keep the outcome shape clean, the `ConflictDetected` field reflects ONLY the fresh-conflict signal from this handshake (`Trigger != None`). The `alreadyFenced` propagation case sets `ConflictDetected = false, Trigger = None` and the caller (which already knows the volume is fenced) writes the `ConflictObserved=true` marker regardless. This means the handshake's job is purely "did this open detect a fresh conflict?", and the caller composes that with the persisted state.

   Restating the algorithm with this cleanup:

   **3'. If `conflictDetected` is true (fresh detection):**
   - Build witness payload with `ConflictObserved = true`.
   - Write to all accessible tails.
   - Return outcome with `ConflictDetected = true, Trigger = (the triggering trigger), …`.

   **4'. Else if `alreadyFenced` is true (persisted fence, no fresh trigger):**
   - Build witness payload with `ConflictObserved = true` (continue marking the tails until a `PromoteAsync` resets the local fence).
   - Write to all accessible tails.
   - Return outcome with `ConflictDetected = false, Trigger = None, …`.

   **5'. Else (no conflict, no prior fence):**
   - Build witness payload with `ConflictObserved = false`.
   - Write to all accessible tails.
   - Return outcome with `ConflictDetected = false, Trigger = None, …`.

6. Tails where `WitnessStore.WriteAsync` fails are logged at `Warning` and counted separately (`TailsWritten`). A write failure to a tail is not a split-brain; it is an upload problem (normal tail-availability degradation). The volume proceeds normally.

The handshake's `ConflictDetected = true` puts the volume into fenced state. This is handled by the caller (`FlashSkinkVolume.OpenAsync` → `RunWitnessHandshakeAsync`), not by `WitnessHandshake` itself, because the brain write for `Settings["VolumeState"]` is owned by the orchestrating layer.

---

#### Files to modify

**`src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs`** (three changes — `BackfillAndStampOnOpenAsync` already returns `(VolumeState, long)` from §3.5.1):

**Change 1 — Integrate `WitnessHandshake.RunAsync` into `OpenAsync`:**

After `BackfillAndStampOnOpenAsync` returns `(state, newEpoch)`, and before `BuildVolumeFromSessionAsync`, add:

```csharp
// Session-begin witness handshake (Blueprint §19.6, Principle 35).
// At this point: the single-instance lock is held, the brain connection is open
// and migrated, the epoch has been incremented, and ownership has NOT yet
// transferred to BuildVolumeFromSessionAsync. If the handshake fails (e.g. the
// brain write for fenced state throws), the finally block disposes session and lock
// as normal — no partial-state is left behind.
var handshakeResult = await RunWitnessHandshakeAsync(
    session, skinkRoot, options, newEpoch, volumeState, ct).ConfigureAwait(false);
if (!handshakeResult.Success)
{
    return Result<FlashSkinkVolume>.Fail(handshakeResult.Error!);
}
volumeState = handshakeResult.Value;   // may have changed from Normal→Fenced
```

`RunWitnessHandshakeAsync` is a new private static method:

```csharp
private static async Task<Result<VolumeState>> RunWitnessHandshakeAsync(
    VolumeSession session,
    string skinkRoot,
    VolumeCreationOptions options,
    long newEpoch,
    VolumeState currentState,
    CancellationToken ct)
```

It:
1. Reads `Settings["VolumeID"]` from `session.BrainConnection` (Dapper, fine — not a hot path).
2. Reads the list of active providers from the brain `Providers` table.
3. For each active `Providers` row, resolves the `IStorageProvider` from `options.ProviderRegistry`. Providers not in the registry are skipped (normal for Phase 3 tests using `InMemoryProviderRegistry` — the registry is populated by `RegisterTailAsync` after volume creation, not before open).
4. Calls `WitnessHandshake.RunAsync(tails, volumeId, newEpoch, dek, alreadyFenced: currentState == VolumeState.Fenced, ct)`.
5. **Transition table — combine `outcome.ConflictDetected` and `currentState`:**
   - **Fresh conflict detected & currently Normal** (`outcome.ConflictDetected && currentState == Normal`): write `Settings["VolumeState"] = "Fenced"`. Publish a `Critical` notification to `options.NotificationBus` with user-vocabulary message: "FlashSkink detected that this skink has a conflicting copy on one of your tails. Uploads are paused until you resolve the conflict." (Principle 25 — strictly user-vocabulary; do NOT use "epoch", "witness", "split-brain", "volume" in this string.) Write a `BackgroundFailures` row (Principle 24): severity `Critical`, message including `outcome.ConflictWitness?.ToDisplayString()` and the trigger kind in metadata, code `ErrorCode.SplitBrainDetected`. Log at `Error` (Principle 27 — log at the construction site of the Fail). Return `Result<VolumeState>.Ok(Fenced)`.
   - **Fresh conflict detected & currently Fenced** (`outcome.ConflictDetected && currentState == Fenced`): the volume was already fenced; the handshake just discovered another fresh trigger (e.g., the other skink ran ANOTHER session while we were closed). No state change. No new `Critical` notification (avoid spam — Principle 24 applies to the *initial* detection, not every re-detection). Optionally log at `Warning` for diagnostic visibility. Return `Result<VolumeState>.Ok(Fenced)`.
   - **No fresh conflict & currently Fenced — AUTO-DOWNGRADE check**: this is the crash-recovery path for a `PromoteAsync` that crashed mid-flight. If the brain says Fenced but the handshake walked every reachable tail and observed no `ConflictObserved=true` markers AND no epoch conflicts AND at least one tail was actually read (`outcome.TailsRead > 0`), the persisted Fenced row is stale. Downgrade: write `Settings["VolumeState"] = "Normal"`, log at `Information` ("Auto-downgraded stale Fenced state — a prior promote completed the tail writes before persisting the local state change."), publish an `Info` notification ("FlashSkink finished resolving a prior conflict; uploads are resuming."), return `Result<VolumeState>.Ok(Normal)`. If `outcome.TailsRead == 0` (no reachable tails to confirm against), keep the Fenced state — auto-downgrade requires positive evidence, not absence of evidence.
   - **No fresh conflict & currently Normal**: ordinary path, no change. Return `Result<VolumeState>.Ok(Normal)`.

**Change 2 — Pass `volumeState` to `BuildVolumeFromSessionAsync` and on to `UploadQueueService`:**

`BuildVolumeFromSessionAsync` gains a `bool initiallyFenced` parameter (not a `VolumeState` to avoid coupling the build helper to the `Identity` namespace through the constructor; the enum is referenced freely elsewhere in the file). `UploadQueueService` constructor gains `bool initiallyFenced`. The `UploadQueueService` sets `_fenced = initiallyFenced ? 1 : 0` in its constructor. `BuildVolumeFromSessionAsync` also receives the `ILogger<WitnessHandshake>` from `options.LoggerFactory` and constructs the `WitnessHandshake` + `WitnessStore` instances. The `FlashSkinkVolume` constructor gains an `IWitnessStore` (or `WitnessStore`) field plus the DEK reference (already available via `_session.Dek`) so that `PromoteAsync` (§3.5.3) can drive fresh writes without rebuilding services.

**Change 3 — `RegisterTailAsync` writes an initial witness to the freshly-registered tail.**

After the brain insert + in-memory registration succeed (and before pulsing the wakeup signal), `RegisterTailAsync` constructs a witness payload via `WitnessPayload.ForNewSession(volumeId, currentEpoch, appVersion)` with `ConflictObserved` matching the current `State`, and calls `WitnessStore.WriteAsync(provider, _session.Dek, payload, CancellationToken.None)`. Rationale: without this, the tail would have blobs and brain mirrors but no witness until the *next* `OpenAsync`. A clone of the USB opened during that window would see "no witness on this tail" and treat as first use — silently missing the conflict. Writing the witness on registration closes the window.

Write failures are logged at `Warning` but do NOT fail the `RegisterTailAsync` call. The brain row is committed, the registry has the provider, and the volume continues — the next session-begin handshake will retry the witness write. (Bias: prefer "tail registered with delayed witness" over "tail not registered" when the only failure was a transient provider error.)

The witness write uses `CancellationToken.None` per Principle 17: once the brain row is committed and the registry is populated, the witness write is the atomic-commit step of "this tail is now part of this volume". The entry-point `ct.ThrowIfCancellationRequested()` already runs before the brain insert.

**`src/FlashSkink.Core/Upload/UploadQueueService.cs`** (two changes):

**Change 1 — Constructor gains `bool initiallyFenced`:**

Add `private volatile int _fenced;` field. In constructor: `_fenced = initiallyFenced ? 1 : 0`. Volatile `int` instead of `bool` so `Interlocked.Exchange` can be used for atomic updates.

**Change 2 — `SetFenced(bool fenced)` method and per-worker guard:**

```csharp
/// <summary>
/// Enables or disables the fenced-state guard for Phase 2 uploads. When fenced,
/// all workers idle until unfenced. Called by <see cref="FlashSkinkVolume.PromoteAsync"/>
/// and by the witness handshake on split-brain detection. Thread-safe.
/// </summary>
internal void SetFenced(bool fenced)
    => Interlocked.Exchange(ref _fenced, fenced ? 1 : 0);
```

In the orchestrator loop and every worker loop: at the top of the active body (after the network-availability check), add:

```csharp
if (_fenced != 0)
{
    // Volume is fenced — skip all upload activity until PromoteAsync clears the flag.
    // No notification here; the fenced notification was surfaced on the session open that
    // detected the conflict. (Principle 24 applies to the initial detection, not to every
    // subsequent idle tick.)
    await Task.Delay(WorkerIdle, workerCt).ConfigureAwait(false);
    continue;
}
```

This returns the worker immediately to its delay-and-wait loop, burning no CPU or network while fenced.

When the `UploadQueueService` returns `Fail(VolumeFenced)` from an upload attempt invoked while fenced... wait — actually, the guard is inside the background worker, not inside a public method. `UploadQueueService` does not have a public `UploadAsync` — the upload is driven by the internal workers. The `VolumeFenced` error code is returned by any Phase 2 path that might be invoked directly in the future; for now the workers simply idle. The `ErrorCode.VolumeFenced` will be used in §3.5.3 if `PromoteAsync` is called on an already-promoting volume (race guard).

**`BLUEPRINT.md`** — add two new subsections under §19:

**§19.6 — Witness protocol**

Documents:
- Purpose: detect diverged clones before any upload activity begins
- The two-file design analogy (`instance.lock` / `instance.manifest` → `instance.lock` / `witness.enc`): the witness is the tail-side dual of the brain-side epoch
- Witness path: `_witness/current.enc` on each tail
- Encryption: AES-256-GCM with the DEK; binary envelope format
- VolumeEpoch: one increment per session open; the authoritative conflict signal (wall-clock timestamps are informational only)
- Handshake flow: read → compare → write (with `CancellationToken.None` in write)
- Two-sided fence: winner stamps `ConflictObserved=true`; lagger detects via marker even if its own epoch is lower
- Offline tails: skipped silently — no information is not a conflict
- Volume ID validation: witness `VolumeId` field must match the opening brain's `Settings["VolumeID"]`; mismatch treated as "no information from this tail" (same as absent witness)
- Tail registration: `RegisterTailAsync` writes an initial witness immediately so the witness contract holds from registration onward, not just from the second session open after registration

**Known limitation — simultaneous cross-host opens at the same prior epoch are undetectable.**
If two skinks share the same `oldEpoch = N` and open *at the exact same moment* on different hosts (the single-instance lock from §19.5 prevents this within a host but not across hosts when the USB is mounted via a network share), the following race is possible:
1. Skink A reads witness (epoch N or earlier — no conflict signal).
2. Skink B reads witness (epoch N or earlier — no conflict signal).
3. Skink A writes witness with epoch N+1 and its own SessionId.
4. Skink B writes witness with epoch N+1 and a *different* SessionId — overwriting A's witness.
5. Neither detects a conflict on this open; both believe they wrote the canonical epoch N+1.
6. On A's next open (say to epoch N+2), it reads the witness and sees epoch N+1 with B's SessionId — but the algorithm doesn't compare SessionIds, only epochs, and `N+1 < N+2` looks fine.

This race requires (a) network-mounted USB, (b) literal simultaneous opens, and (c) survival of (a)+(b) without the single-instance lock catching it. The protocol's primary threat model is sequential clone use — clone in one place, use in another, plug back in — where the conflict IS detected on the next open. The simultaneous-cross-host case is documented as a known limitation; the operational guidance is "do not mount a FlashSkink USB via a network share." A future enhancement could store per-tail `LastWitnessSessionId` in the brain to detect this case, but that complication is deferred until empirical evidence shows network-mounted USB use is common enough to matter.

**§19.7 — Fenced state**

Documents:
- Two parallel conflict triggers, both evaluated on every handshake:
  1. **Epoch comparison**: `witness.Epoch >= localNewEpoch` from any reachable tail
  2. **Conflict marker**: `witness.Epoch < localNewEpoch` AND `witness.ConflictObserved == true` from any reachable tail (the lagger learning the winner saw us)
- What fencing means: Phase 2 uploads blocked on ALL tails (not just the conflicting one); Phase 1 writes and reads unaffected
- Persistence: `Settings["VolumeState"] = "Fenced"` survives close/reopen
- Two-sided fence: when fenced (freshly or persisted), every handshake writes `ConflictObserved = true` to every accessible tail, so the other skink's next handshake picks up the marker from any tail it can reach
- Auto-downgrade (crash recovery): when the brain says Fenced but the handshake observes no conflict signals AND at least one tail was actually read, the persisted Fenced row is treated as a stale artefact from a crashed `PromoteAsync` and downgraded to Normal. Requires positive evidence (a reachable tail with a clean witness); absence of reachable tails keeps the Fenced state.
- User visibility: `Critical` notification + `BackgroundFailures` row on FIRST detection only; re-detections while already fenced do not re-publish. Auto-downgrade publishes an `Info` notification. CLI surfaces fenced state at next prompt in Phase 4.

---

#### Test spec

**`tests/FlashSkink.Tests/Identity/WitnessHandshakeTests.cs`** — class `WitnessHandshakeTests`

Uses a real `WitnessStore` backed by one or more `FileSystemProvider` instances in temp dirs.

- `RunAsync_NoPriorWitness_WritesWitness_ReturnsNoConflict` — first open; tail has no witness; handshake writes witness; outcome `ConflictDetected = false`.
- `RunAsync_WitnessEpochLower_ReturnsNoConflict` — tail has a witness with `Epoch = 3`; `newEpoch = 5`; no conflict.
- `RunAsync_WitnessEpochEqual_ReturnsConflict` — tail witness `Epoch = 5`, `newEpoch = 5`; conflict detected (another holder wrote epoch 5 first). *Note: equal-epoch means the other party also opened a session that reached epoch 5. We are the collision.*
- `RunAsync_WitnessEpochHigher_ReturnsConflict` — tail witness `Epoch = 7`, `newEpoch = 5`; conflict.
- `RunAsync_ConflictDetected_WritesWitnessWithConflictObservedTrue` — after a conflict, the written witness has `ConflictObserved = true` on all accessible tails.
- `RunAsync_AlreadyFenced_NoConflictOnTail_WritesConflictObservedTrue` — `alreadyFenced = true`, tail shows no higher epoch; the written witness still has `ConflictObserved = true`.
- `RunAsync_UnreachableTail_SkipsAndReturnsNoConflict` — `FaultInjectingStorageProvider` returns `ProviderUnreachable` on `ListAsync`; outcome `ConflictDetected = false`, `TailsRead = 0`.
- `RunAsync_PartiallyUnreachable_ConflictsOnReachableTail_ReturnsConflict` — two tails; one unreachable, one with high epoch; conflict detected.
- `RunAsync_VolumeIdMismatch_SkipsTail` — witness was written for a different `VolumeId`; treated as absent; no conflict.
- `RunAsync_CancelledBeforeStart_ReturnsCancelled`

**`tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs`** — class `SplitBrainIntegrationTests`

Full end-to-end tests via `FlashSkinkVolume`. Requires a registered provider (use `FileSystemProvider` via `RegisterTailAsync`).

- `OpenAsync_DetectsSplitBrain_VolumeFenced` — create a volume, register a tail, open+close (epoch 2, witness written). Manually write a witness with epoch 10 to the tail's `_witness/current.enc`. Reopen the volume; assert the returned volume exposes `VolumeState.Fenced`. (Expose `VolumeState` as a `public` property on `FlashSkinkVolume` — see API surface below.)
- `OpenAsync_SplitBrain_PublishesCriticalNotification` — same setup; assert `INotificationBus` received a `Critical` notification.
- `OpenAsync_SplitBrain_WritesBackgroundFailureRow` — assert `BackgroundFailures` table has a row with `SplitBrainDetected`.
- `OpenAsync_FencedState_PersistedAcrossReopen` — detect split-brain → close → reopen (without manipulating the witness again); assert `VolumeState.Fenced` on second open.
- `OpenAsync_FencedState_UploadsAreBlocked` — write a file after fencing; assert upload queue service does not write to the provider (witness to this: `FaultInjectingStorageProvider` records calls; `BeginUploadAsync` is never called for the file blob).
- `OpenAsync_FencedState_Phase1WritesSucceed` — write a file while fenced; assert `WriteFileAsync` returns `Ok`.
- `TwoSidedFence_LaggerSeesConflictObservedMarker_TriggersFence` — simulate the lagger scenario: write a witness with `ConflictObserved = true` and `Epoch = 3` to a tail; open a volume whose local `newEpoch = 5` (higher than the witness epoch — so the epoch trigger does NOT fire). Assert the volume is fenced via the marker trigger: `outcome.Trigger == ConflictMarker`. This is the direct test for meta-plan item 12 (two-sided fence) — without the marker trigger, the lagger would never learn it was the lagger.
- `TwoSidedFence_WinnerWritesConflictObservedMarker` — set up a fresh-conflict scenario (witness epoch ≥ newEpoch). After `OpenAsync` returns Fenced, decrypt the witness on the tail and assert `ConflictObserved == true`. This proves the winner stamps the marker so the lagger's next handshake sees it.
- `Notification_SplitBrainDetected_UsesUserVocabularyOnly` — capture the published `Critical` notification's message string. Assert it contains "skink" and "tail"; assert it does NOT contain any of "epoch", "witness", "split-brain", "split brain", "volume", "fence", "fenced" (Principle 25). Same assertion for the `BackgroundFailures` row's `Message` column.
- `AutoDowngrade_StaleFencedBrainRow_WitnessShowsNoConflict_DowngradesToNormal` — direct-write `Settings["VolumeState"] = "Fenced"` to a freshly-created volume's brain; write a witness with `Epoch = 2`, `ConflictObserved = false` to the tail; open the volume with `newEpoch = 5`. Assert: `State == Normal` after `OpenAsync`; `Settings["VolumeState"]` row is now `"Normal"`; an `Info` notification was published.
- `AutoDowngrade_NoTailsReachable_KeepsFencedState` — same setup, but make all tails unreachable. Assert: `State == Fenced` (auto-downgrade requires positive evidence; absence of evidence is not evidence of absence).
- `FreshConflictWhileAlreadyFenced_DoesNotRePublishNotification` — set up a volume already in `Fenced` state. Cause a fresh conflict detection. Assert: no NEW `Critical` notification was published; no NEW `BackgroundFailures` row was written. (Principle 24 — initial detection only.)

**New public API on `FlashSkinkVolume`:**

```csharp
/// <summary>
/// The current operational state of this volume. <see cref="VolumeState.Fenced"/> indicates
/// that a split-brain conflict was detected during the session-begin handshake; Phase 2
/// uploads are blocked. Resolved by <see cref="PromoteAsync"/>. (Blueprint §19.7.)
/// </summary>
public VolumeState State { get; private set; }
```

Set immediately after `RunWitnessHandshakeAsync` returns, before `BuildVolumeFromSessionAsync`. `PromoteAsync` (§3.5.3) updates it to `Normal`.

---

#### Line-of-code budget

| File | Lines |
|---|---|
| `src/FlashSkink.Core/Identity/WitnessHandshake.cs` | ~200 |
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~150 |
| `src/FlashSkink.Core/Upload/UploadQueueService.cs` (delta) | ~30 |
| `BLUEPRINT.md` (delta) | ~90 |
| **Total src delta** | **~470** |
| `tests/.../Identity/WitnessHandshakeTests.cs` | ~230 |
| `tests/.../Orchestration/SplitBrainIntegrationTests.cs` | ~320 |
| **Total test delta** | **~550** |

Budget rationale (revised up from initial draft): the `FlashSkinkVolume` delta carries `RunWitnessHandshakeAsync` (read providers from brain, look up registry, build the tails list, invoke handshake, persist state transition, publish notification, write BackgroundFailures row) plus the `RegisterTailAsync` witness-write addition plus the `State` property plumbing. ~80 lines turned out to be optimistic at draft time — 120–150 is realistic.

---

#### Principles touched

- **P1, P13, P14, P15, P16, P17** — same shape as §3.5.1, applied to `WitnessHandshake`, `RunWitnessHandshakeAsync`, and the `RegisterTailAsync` modification. The witness-write step in both `RunWitnessHandshakeAsync` and `RegisterTailAsync` uses `CancellationToken.None` at every await site (P17).
- **P22** (Dapper outside hot paths) — reading the `Providers` table and writing `Settings["VolumeState"]` use Dapper. Both happen at session-open time, not on the upload hot path.
- **P24** (no silent background failures) — `Critical` notification + `BackgroundFailures` row on first conflict detection. Subsequent re-detections while already Fenced do NOT publish again (avoid notification spam — the user has been told once).
- **P25** (appliance vocabulary) — every user-facing string uses "skink"/"tail"/"copy"/"resolve" only. Asserted by `Notification_SplitBrainDetected_UsesUserVocabularyOnly`. The `ErrorCode.SplitBrainDetected` *name* is internal and acceptable (it does not appear in user-visible strings).
- **P26** (no secrets in logs) — `ErrorContext.Metadata` keys are `ConflictingProviderId`, `WitnessEpoch`, `LocalEpoch`, `WitnessHost`, `WitnessSessionId`, `ConflictTrigger`. None match `*Token`, `*Key`, `*Password`, `*Secret`, `*Mnemonic`, `*Phrase`.
- **P27** (Core logs at the construction site of the Fail) — `RunWitnessHandshakeAsync` logs at `Error` when constructing the Fenced transition; `UploadQueueService`'s fenced-guard skip is silent (no log per tick — only the original detection logs).
- **P28** (MEL-only logging) — same as §3.5.1.

#### Non-goals for §3.5.2

- Do NOT add `PromoteAsync` (§3.5.3).
- Do NOT handle the edge cases of partial witness on disk, restore-from-image analysis, or same-phrase-separate-init in code (those are §3.5.3 tests and docs).
- Do NOT add `PromoteAsync` guards in `UploadQueueService` (`VolumeFenced` error on attempts — that is only relevant when `UploadAsync` becomes a direct call path, which does not exist in Phase 3).
- Do NOT add per-tail `LastWitnessSessionId` tracking for the simultaneous-cross-host case — documented as a known limitation in §19.6; reserved for a future enhancement if empirical evidence warrants it.

---

### §3.5.3 — Resolution flow and edge-case hardening

**Blueprint sections to read:** §19.5 (lock lifecycle context), §19.6–19.7 (written in §3.5.2 — read as the prior art for §3.5.3's additions), §8.5 (notification on resolution — `Info` notification when unfenced), §21.3 (crash-consistency — ensure `PromoteAsync` is crash-safe).

**Scope summary:** One new public method on `FlashSkinkVolume`; targeted edge-case test coverage for scenarios identified in the meta-plan; Blueprint §19.8; no new types.

---

#### Files to modify

**`src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs`** — add `PromoteAsync`:

```csharp
/// <summary>
/// Resolves a split-brain conflict by declaring this skink canonical. Clears the fenced
/// state, writes a fresh witness (with <c>ConflictObserved = false</c>) to every accessible
/// tail, and resumes Phase 2 uploads. Idempotent — safe to call on an unfenced volume
/// (returns <see cref="Result.Ok"/> immediately).
/// </summary>
/// <remarks>
/// The demoted skink (the one not promoted) retains its <c>Settings["VolumeState"] = "Fenced"</c>
/// and will see <c>ConflictObserved = false</c> in the fresh witness on its next session open.
/// A fenced volume that opens and reads <c>ConflictObserved = false</c> with an epoch it
/// recognises as ahead of its own knows the winner has promoted: it should display a user
/// message recommending recovery via a tail rather than attempting to promote itself
/// (this guidance is surfaced as a notification; the volume still works for Phase 1 and reads).
/// (Blueprint §19.8.)
/// </remarks>
public async Task<Result> PromoteAsync(CancellationToken ct = default)
```

Implementation:
1. `ThrowIfDisposed()`.
2. Acquire the gate (`_gate.WaitAsync(ct)`).
3. If `State == VolumeState.Normal`: release gate, return `Result.Ok()` immediately (idempotent on local state). **Note on idempotency vs. stale-tail repair:** an offline tail at the time of a prior promote retains its old `ConflictObserved=true` witness. The current method does NOT re-attempt the write — that repair is performed automatically by the next `OpenAsync`'s handshake: with `alreadyFenced=false` post-promote, the handshake writes a fresh `ConflictObserved=false` witness to every accessible tail (including the previously-offline one if it is now reachable). This keeps `PromoteAsync` cheap and lets the recurring session-open handshake act as the eventual-consistency repair loop.
4. Call `RunPromoteAsync(session, options, dek, ct)` — private static:
   a. Read `Settings["VolumeID"]` and `Settings["VolumeEpoch"]` from the brain.
   b. Build the fresh witness payload: `WitnessPayload.ForNewSession(volumeId, epoch, appVersion)` with `ConflictObserved = false`.
   c. For each accessible tail (enumerated from `options.ProviderRegistry` and the `Providers` brain table — same lookup logic as `RunWitnessHandshakeAsync`): `WitnessStore.WriteAsync(provider, dek, payload, CancellationToken.None)`. Write failures logged at `Warning`, not fatal — see step 3 note (the next handshake handles repair).
   d. Write `Settings["VolumeState"] = "Normal"` to the brain (single upsert).
5. `_uploadQueueService.SetFenced(false)`.
6. `State = VolumeState.Normal`.
7. `_wakeupSignal.Pulse()` (wake upload workers immediately so uploads resume without waiting for the next 60-second poll).
8. Publish `Info` notification: "Conflict resolved. FlashSkink will now resume uploading to your tails." (Principle 25 vocabulary — no "fence", "epoch", "witness", "volume", "split-brain").
9. Release gate.
10. Catch ordering: `OperationCanceledException` → `Cancelled`; `SqliteException` → `DatabaseWriteFailed`; `Exception` → `Unknown`.

**Crash-safety — interaction with §3.5.2's auto-downgrade.** If the process crashes after step 4c (witness writes succeed) but before step 4d (brain `Settings["VolumeState"] = "Normal"` is written), the brain still reads `Fenced` on the next open while the witnesses on the tails already show `ConflictObserved=false` with an advanced epoch. The §3.5.2 auto-downgrade path in `RunWitnessHandshakeAsync` detects exactly this: `currentState == Fenced && outcome.ConflictDetected == false && outcome.TailsRead > 0` → write `Settings["VolumeState"] = "Normal"` and return `Normal`. The implementation lives in §3.5.2; §3.5.3 only references it. This makes `PromoteAsync` crash-safe without requiring a brain-side journal of the promote operation.

**`BLUEPRINT.md`** — add §19.8 — Resolution flow:

Documents:
- `PromoteAsync` semantics: canonical declaration, not a merge. The promoted skink keeps its local writes and resumes uploads; the demoted skink retains its writes locally but is blocked from uploading.
- What happens to the demoted skink: on its next open, the handshake reads the winner's fresh witness (`ConflictObserved=false`, advanced epoch). The demoted skink's `oldEpoch` is now behind the witness's epoch — the epoch trigger fires, fencing it again. User guidance: either re-promote (override the prior decision and stamp the marker back at the original winner) or recover from a tail (Phase 5) to bring the volume forward to the winning state. These are fundamentally different actions — re-promote keeps local writes and discards the winner's; recovery discards local writes and adopts the winner's. The CLI in Phase 4 must present this choice explicitly; the choice itself is documented here as the resolution UX.
- Crash-safety reference: the §3.5.2 auto-downgrade path in `RunWitnessHandshakeAsync` heals the `Settings["VolumeState"] = "Fenced"` row when the witnesses show no conflict and at least one tail was reachable. This makes `PromoteAsync` crash-safe without a brain-side journal. See §19.7 for the auto-downgrade definition; this section cross-references it without re-stating.
- The "same-phrase-separate-init" case: two volumes initialised from the same recovery phrase have different `VolumeIDs` (Principle 33) and therefore distinct tail-path prefixes on any shared provider account → no witness interaction, no conflict. Documented as by-design.
- The "restore-from-image-as-clone" case: structurally identical to a USB clone (same VolumeID, same epoch) → detected by the standard epoch comparison the first time either copy advances. Documented as by-design.
- Clock skew: timestamps in the witness are informational; the epoch counter is the authoritative signal; clock skew cannot cause false positives or negatives. Documented as by-design.
- The "no merge" rule: `PromoteAsync` does NOT reconcile differing file contents between the two skinks. Files written only on the demoted skink remain only on the demoted skink (and on whatever tails got them before fencing). The product surface for "bring the demoted writes forward" is Phase 5 recovery, not a witness-protocol feature.

---

#### Test spec

**`tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs`** (continued, new methods in the same class):

- `PromoteAsync_ClearsFencedState_ReturnsNormal` — fence the volume (via fake witness), call `PromoteAsync`, assert `State == Normal`.
- `PromoteAsync_WritesFreshWitnessWithNoConflictMarker` — after promote, read the witness from the tail; assert `ConflictObserved = false`.
- `PromoteAsync_ResumesUploads` — write a file while fenced (queued but not uploaded); call `PromoteAsync`; wait for the upload wakeup to fire; assert the provider received a `BeginUploadAsync` call for the file.
- `PromoteAsync_OnUnfencedVolume_ReturnsOk` — no conflict set up; `PromoteAsync` returns `Ok` immediately.
- `PromoteAsync_IsIdempotent` — call `PromoteAsync` twice in a row; both return `Ok`; no double-notification.
- `PromoteAsync_OfflineTails_StillClearsLocalState` — tail is unreachable; `PromoteAsync` succeeds; `State == Normal`; the offline tail's stale `ConflictObserved=true` witness will be repaired by the next session-open handshake (covered by `OpenAsync_AfterPromote_OfflineTailNowOnline_RepairsWitness` below).
- `OpenAsync_AfterPromote_OfflineTailNowOnline_RepairsWitness` — promote with one tail offline; close volume; bring the previously-offline tail online; reopen volume. Assert: the previously-offline tail now has a witness with `ConflictObserved = false` (the handshake wrote the repaired witness as part of normal flow).
- `OpenAsync_CrashRecovery_BrainSaysFenced_WitnessShowsNoConflict_AutoDowngrades` — write `Settings["VolumeState"] = "Fenced"` directly to the brain (simulating a crash mid-promote); write a witness with low epoch and `ConflictObserved = false`; open the volume; assert `State == Normal` (auto-downgrade triggered). Implementation of the auto-downgrade lives in §3.5.2's `RunWitnessHandshakeAsync`; the test in §3.5.3 covers the full crash-recovery story.
- `PromoteAsync_Notification_UsesUserVocabularyOnly` — capture the `Info` notification's message. Assert it contains "skink" or "tail"; assert it does NOT contain any of "epoch", "witness", "fence", "fenced", "split-brain", "split brain", "volume" (Principle 25).

**Edge-case tests (new file): `tests/FlashSkink.Tests/Identity/WitnessEdgeCaseTests.cs`** — class `WitnessEdgeCaseTests`

These exercise the `WitnessStore` + `WitnessHandshake` combination:

- `PartialWitnessFile_TruncatedDuringWrite_TreatedAsAbsent` — write a partial (truncated) `.enc` file to the tail's `_witness/` directory manually; `TryReadAsync` returns `(success: true, value: null)`; handshake treats as no information, no conflict.
- `WitnessDeletion_ManualDelete_TreatedAsFirstUse` — write witness, then manually delete the `_witness/current.enc` file from the provider's backing store; next `TryReadAsync` returns null; handshake proceeds as first use (writes fresh witness, no conflict).
- `SamePhraseSeperateInit_DifferentVolumeIds_NoWitnessInteraction` — conceptual test: create two volumes with distinct temp roots, open a fake "shared" provider on a common path, register it on both. Volume A opens and writes its witness (volumeId = A). Volume B opens and reads the provider's witness — the VolumeId mismatch (A ≠ B) causes the handshake to skip this witness. No conflict. Assert `State == Normal` for both. *This test documents the by-design behaviour: different VolumeIDs → no witness interaction even on a shared provider.*
- `RestoreFromImage_IdenticalVolumeIdAndEpoch_DetectedOnNextOpen` — a restore-from-image scenario is structurally identical to a USB clone: two volumes share the same VolumeID and the same epoch. After the restore, one of them opens first (epoch N→N+1, writes witness epoch N+1 to the tail). The other opens next (epoch N→N+1, reads witness epoch N+1 from the tail): `payload.Epoch == newEpoch` → conflict detected. Assert `State == Fenced`. *This test documents that restore-from-image is handled by the standard protocol.*
- `ClockSkew_LargeTimestampDifference_DoesNotAffectConflictDetection` — create a witness with `CommittedAtUtc` in the far future (+100 years); epoch is still 1; local new epoch is 2; no conflict (clock skew is irrelevant; only epoch is checked).

---

#### Line-of-code budget

| File | Lines |
|---|---|
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~110 |
| `BLUEPRINT.md` (delta) | ~70 |
| **Total src delta** | **~180** |
| `tests/.../Orchestration/SplitBrainIntegrationTests.cs` (new methods) | ~230 |
| `tests/.../Identity/WitnessEdgeCaseTests.cs` | ~150 |
| **Total test delta** | **~380** |

---

#### Principles touched

- **P1, P13, P14, P15** — `PromoteAsync` is a public Core API; returns `Result`; `ct` last; OCE-first catch.
- **P17** — every tail-write call inside `PromoteAsync` passes `CancellationToken.None` at the await site (the entry-point `ct.ThrowIfCancellationRequested()` is the only cancellation check).
- **P24** — `PromoteAsync` publishes an `Info` notification on success. No `BackgroundFailures` row is written (resolution is not a failure). Failures inside `PromoteAsync` (e.g., DatabaseWriteFailed during the Settings update) return `Result.Fail` to the caller; the volume stays in `Fenced` state until the user retries.
- **P25** (appliance vocabulary) — `PromoteAsync`'s `Info` notification text is asserted by `PromoteAsync_Notification_UsesUserVocabularyOnly` (uses "skink"/"tail"; does not use "epoch", "witness", "fence", "split-brain", "volume").
- **P27** — `PromoteAsync` logs at `Information` on entry (`"User-initiated promote — clearing fenced state"`) and at `Information` on success.
- **P30** (crash-consistency invariant preserved) — `PromoteAsync`'s crash-window is recovered by §3.5.2's auto-downgrade. No new invariant is introduced. The §3.5.3 edge-case test `OpenAsync_CrashRecovery_BrainSaysFenced_WitnessShowsNoConflict_AutoDowngrades` proves the round-trip.

#### Non-goals for §3.5.3 (and for Phase 3.5 as a whole)

- Do NOT implement the `flashskink clone` CLI command (deferred to V1.x / V2 — meta-plan item 15).
- Do NOT implement `siblings` tracking or a `Siblings` brain table (same deferral).
- Do NOT implement automatic recovery from a tail after demotion — the user is directed to recovery (Phase 5) if they want the demoted skink's contents brought forward. `PromoteAsync` is the exit from the winning side; recovery is the exit from the losing side.
- Do NOT implement OAuth revocation handling in `WitnessStore` beyond what is already in `IStorageProvider` (revocation returns `TokenRevoked`, which `TryReadAsync` maps to "tail offline, skip"). No special revocation protocol in Phase 3.5.
- Do NOT add `WitnessHandshake` to `CreateAsync` — a brand-new volume has no tails registered at creation time; the registered-provider list will always be empty; the handshake would be a no-op. The epoch is seeded and incremented in `SeedInitialSettingsAsync` + `BackfillAndStampOnOpenAsync` which runs on both `CreateAsync` and `OpenAsync`. The witness handshake runs only in `OpenAsync` (where tails may be registered).
- Do NOT add auto-update or phone-home functionality (Principle 32, permanent prohibition).

---

## Acceptance criteria for the full phase

- [ ] `dotnet build` clean (`--warnaserror`) on all supported RIDs after each PR.
- [ ] All new tests pass; no existing tests regress.
- [ ] `dotnet format --verify-no-changes` clean after each PR.
- [ ] After §3.5.3: two `FileSystemProvider`-backed volumes opened on the same epoch produce a conflict, and `PromoteAsync` on the winner lets it upload while the loser stays blocked.
- [ ] After §3.5.3: `BLUEPRINT.md` contains §19.6, §19.7, and §19.8.
- [ ] No new assembly references introduced (all BCL; no new NuGet packages required).
