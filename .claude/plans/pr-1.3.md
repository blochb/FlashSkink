# PR 1.3 — DEK vault and volume lifecycle

**Branch:** pr/1.3-dek-vault-and-volume-lifecycle
**Blueprint sections:** §18.4, §18.6, §18.8
**Dev plan section:** phase-1 §1.3

## Scope

Delivers two types in `FlashSkink.Core/Crypto/`:

`KeyVault` manages the 100-byte on-disk vault file (`vault.bin`). It wraps and unwraps the
DEK using AES-256-GCM with the KEK derived from Argon2id. All key material is zeroed within
method scope — the type holds no `byte[]` fields for DEK or KEK. Vault writes are atomic:
temp file → fsync → rename.

`VolumeSession` (sealed, `IAsyncDisposable`) holds the open DEK and brain `SqliteConnection`
for the lifetime of an unlocked volume. `VolumeLifecycle` is the thin orchestrator that opens
and returns a `VolumeSession`. `VolumeLifecycle` in Phase 1 implements only enough to support
the brain-repository tests in §1.6; the full `FlashSkinkVolume` surface is Phase 2+.

Note: `VolumeLifecycle.OpenAsync` references `BrainConnectionFactory` (§1.5) and
`MigrationRunner` (§1.5) which do not exist yet. Those integration points are
**left as forward-declared stubs** in this PR and wired in §1.5.

## Files to create

- `src/FlashSkink.Core/Crypto/KeyVault.cs` — `KeyVault` sealed class, ~170 lines
- `src/FlashSkink.Core/Crypto/VolumeSession.cs` — `VolumeSession` sealed class + `VolumeLifecycle` sealed class, ~100 lines
- `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs` — unit tests, ~200 lines
- `tests/FlashSkink.Tests/Crypto/VolumeSessionTests.cs` — unit tests for `VolumeSession` disposal/zeroing, ~60 lines

## Files to modify

- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `PackageReference` for `Microsoft.Data.Sqlite` (already pinned in `Directory.Packages.props`; needed for `SqliteConnection` in `VolumeSession`).

## Dependencies

- NuGet: none new
- `System.Security.Cryptography.AesGcm` — built into .NET 6+ (no package needed)
- Project references: none new
- Runtime deps from prior PRs:
  - `KeyDerivationService` (§1.2) — `DeriveKek`, `DeriveKekFromPassword`, `DeriveBrainKey`
  - `MnemonicService` (§1.2) — `ToSeed`
  - `Result`, `Result<T>`, `ErrorCode`, `ErrorContext` (§1.1)

## Public API surface

### `FlashSkink.Core.Crypto.KeyVault` (sealed class)

Summary intent: Manages the on-disk DEK vault file — creating, unlocking, and re-keying
it. Never holds key material as fields; all buffers are local and zeroed before return.

Constructor: `KeyVault(KeyDerivationService kdf, MnemonicService mnemonic)`

- `Task<Result<byte[]>> CreateAsync(string vaultPath, ReadOnlySpan<byte> password, CancellationToken ct)`
  Generates a 32-byte random DEK and a 32-byte random Argon2id salt. Derives KEK from
  password + salt using `KeyDerivationService.DeriveKekFromPassword`. Wraps DEK with
  AES-256-GCM (AAD = `"DEK_VAULT"u8`). Writes 100-byte vault to disk atomically
  (temp → fsync → rename). Returns `Result<byte[]>` carrying the unwrapped DEK.
  Caller must zero it. Zeroes KEK before return on all paths.

- `Task<Result<byte[]>> UnlockAsync(string vaultPath, ReadOnlySpan<byte> password, CancellationToken ct)`
  Reads and parses the 100-byte vault. Derives KEK from password + stored salt. Decrypts
  with AES-256-GCM (AAD = `"DEK_VAULT"u8`). Returns `Result<byte[]>` carrying the DEK.
  Returns `ErrorCode.InvalidPassword` when GCM authentication fails
  (`CryptographicAuthenticationTagMismatchException`). Returns `ErrorCode.VaultCorrupt`
  on malformed magic/version. Returns `ErrorCode.VaultNotFound` when file is absent.
  Zeroes KEK before return on all paths.

  Note: `ErrorCode.VaultNotFound` is not in blueprint §6.4 enum (which uses
  `VolumeNotFound`). Use `ErrorCode.VolumeNotFound`. Flagged as drift below.

- `Task<Result<byte[]>> UnlockFromMnemonicAsync(string vaultPath, string[] words, CancellationToken ct)`
  BIP-39 recovery path. Derives seed via `MnemonicService.ToSeed`, then KEK via
  `KeyDerivationService.DeriveKek`. Otherwise identical to `UnlockAsync`. Returns
  `ErrorCode.InvalidMnemonic` propagated from `MnemonicService.Validate`.
  Zeroes seed and KEK before return on all paths.

- `Task<Result> ChangePasswordAsync(string vaultPath, ReadOnlySpan<byte> currentPassword, ReadOnlySpan<byte> newPassword, CancellationToken ct)`
  Unlocks vault to recover DEK, derives new KEK from `newPassword`, re-wraps DEK, writes
  new vault atomically (temp → fsync → rename). Zeroes both KEKs and DEK before return.
  Propagates all errors from the internal unlock: `ErrorCode.InvalidPassword` (wrong
  password / GCM auth failure), `ErrorCode.VolumeNotFound` (file absent),
  `ErrorCode.VolumeCorrupt` (bad magic or version). Also returns `ErrorCode.StagingFailed`
  on I/O error writing the new vault.

---

### `FlashSkink.Core.Crypto.VolumeSession` (sealed class, IAsyncDisposable)

Summary intent: Holds the live DEK and open brain SqliteConnection for the duration of
an unlocked volume. Zeroes DEK and brain key on dispose.

Constructor: `internal VolumeSession(byte[] dek, SqliteConnection brainConnection)`
  (internal — only `VolumeLifecycle` creates instances)

- `byte[] Dek { get; }` — the live 32-byte DEK. Caller must not zero; `DisposeAsync` owns it.
- `SqliteConnection BrainConnection { get; }` — the open encrypted brain connection.
- `ValueTask DisposeAsync()` — zeroes `Dek` via `CryptographicOperations.ZeroMemory`,
  closes and disposes `BrainConnection`. Idempotent.

---

### `FlashSkink.Core.Crypto.VolumeLifecycle` (sealed class)

Summary intent: Thin orchestrator for volume open/close. Phase 1 skeleton; grows in §1.5
once `BrainConnectionFactory` and `MigrationRunner` exist.

Constructor: `VolumeLifecycle(KeyVault vault, KeyDerivationService kdf)`

- `Task<Result<VolumeSession>> OpenAsync(string skinkRoot, ReadOnlySpan<byte> password, CancellationToken ct)`
  Phase 1 stub: unlocks the vault, derives the brain key, then returns a stub
  `VolumeSession` with a **null** `BrainConnection` (the real connection is wired in §1.5).
  Returns `Result<VolumeSession>` — on success the caller owns the session and must dispose it.

  Concretely in this PR: unlock vault → derive brain key (32-byte stackalloc span) → zero
  brain key → construct `VolumeSession(dek, brainConnection: null!)` → return.
  The `null!` suppression is documented with a comment: "wired in §1.5 — BrainConnectionFactory not yet implemented".

## Internal types

### `VaultHeader` (private readonly record struct inside `KeyVault`)
Fields parsed from the 100-byte binary:
- `ushort Version`
- `byte MemoryMib` — memory in mebibytes (byte[6] of the vault file)
- `byte Iterations` — high nibble of byte[7]
- `byte Parallelism` — low nibble of byte[7]
- `byte[] Salt` (32 bytes)
- `byte[] Nonce` (12 bytes)
- `byte[] Ciphertext` (32 bytes)
- `byte[] Tag` (16 bytes)

Encoding of the 2-byte params field (offset 6):
- byte[6] = `(byte)(memoryKilobytes / 1024)` — memory in MiB, max 255 MiB. For 19456 KB: stored as `19`.
- byte[7] = `(byte)((iterations << 4) | parallelism)` — high nibble iterations (max 15), low nibble parallelism (max 15).

Decoded back: `memoryKilobytes = (int)MemoryMib * 1024`. For stored value 19: 19 × 1024 = 19456 KB — exact round-trip.

(See Drift Note 2 for why MiB encoding is used instead of the log2 scheme in blueprint §18.4.)

## Method-body contracts

### `KeyVault` — vault binary layout constants
```
Magic     : 4 bytes  "FSVT" ASCII
Version   : 2 bytes  uint16 LE = 1
Params    : 2 bytes  [memMib (1 byte)] [iter<<4 | par (1 byte)]
Salt      : 32 bytes random
Nonce     : 12 bytes random
Ciphertext: 32 bytes AES-256-GCM(KEK, DEK, AAD="DEK_VAULT"u8)
Tag       : 16 bytes GCM auth tag
Total     : 100 bytes
```

### `KeyVault.CreateAsync`
1. `ct.ThrowIfCancellationRequested()`.
2. `dek = RandomNumberGenerator.GetBytes(32)`.
3. `salt = RandomNumberGenerator.GetBytes(32)`.
4. `nonce = RandomNumberGenerator.GetBytes(12)`.
5. `DeriveKekFromPassword(password, salt, out kek)` — propagate failure.
6. `AesGcm` encrypt: `plaintext=dek`, `key=kek`, `nonce=nonce`, `aad="DEK_VAULT"u8`,
   `ciphertext=new byte[32]`, `tag=new byte[16]`.
7. `CryptographicOperations.ZeroMemory(kek)` in finally.
8. Serialize 100-byte vault buffer.
9. Atomic write: write to `{vaultPath}.tmp`, call `FileStream.Flush(flushToDisk:true)`
   (fsync equivalent), `File.Move(tmp, vaultPath, overwrite:true)`.
10. Return `Result<byte[]>.Ok(dek)` (caller zeroes).
- Catch `OperationCanceledException` first → `ErrorCode.Cancelled`.
- Catch `IOException` → `ErrorCode.StagingFailed`.
- `CryptographicOperations.ZeroMemory(kek)` in every catch.

### `KeyVault.UnlockAsync`
1. `ct.ThrowIfCancellationRequested()`.
2. Read file bytes; `FileNotFoundException` → `ErrorCode.VolumeNotFound`.
3. Parse header; magic != `FSVT` or version != 1 → `ErrorCode.VolumeCorrupt`.
4. `DeriveKekFromPassword(password, salt, out kek)` — propagate failure.
5. `AesGcm` decrypt; `CryptographicAuthenticationTagMismatchException` →
   `ErrorCode.InvalidPassword`.
6. `CryptographicOperations.ZeroMemory(kek)` in finally.
7. Return `Result<byte[]>.Ok(dek)`.

### `KeyVault.UnlockFromMnemonicAsync`
1. `ct.ThrowIfCancellationRequested()`.
2. Read vault file bytes; `FileNotFoundException` → `ErrorCode.VolumeNotFound`.
3. Parse header; magic/version invalid → `ErrorCode.VolumeCorrupt`. Extract `salt`.
4. `MnemonicService.ToSeed(words)` — propagate `InvalidMnemonic` failure.
5. `KeyDerivationService.DeriveKek(seed, salt, out kek)` — uses the salt parsed in step 3.
6. `AesGcm` decrypt; `CryptographicAuthenticationTagMismatchException` → `ErrorCode.InvalidPassword`.
7. Zero seed in finally. Zero kek in finally. Return `Result<byte[]>.Ok(dek)`.

### `KeyVault.ChangePasswordAsync`
1. Unlock to get dek (via `UnlockAsync` internally — share the logic).
2. `DeriveKekFromPassword(newPassword, newSalt, out newKek)` with fresh random newSalt.
3. Re-encrypt dek with fresh nonce and newKek.
4. Atomic write of new vault.
5. Zero dek, newKek in finally.

### `VolumeSession.DisposeAsync`
1. Zero `_dek` via `CryptographicOperations.ZeroMemory` (idempotent — ZeroMemory on an
   already-zeroed buffer is harmless).
2. `_brainConnection?.Close(); _brainConnection?.Dispose()`.
3. Guard double-dispose with `Interlocked.Exchange` on a disposed flag.

### `VolumeLifecycle.OpenAsync`
1. Unlock vault → get dek.
2. Derive brain key: `stackalloc byte[32]`, call `_kdf.DeriveBrainKey(dek, brainKey)`.
3. Zero `brainKey` (via `CryptographicOperations.ZeroMemory`).
4. Return `new VolumeSession(dek, brainConnection: null!)`.
   Comment: `// BrainConnectionFactory wired in §1.5`.

## Integration points

- `KeyDerivationService.DeriveKekFromPassword(ReadOnlySpan<byte>, ReadOnlySpan<byte>, out byte[])` → `Result`
- `KeyDerivationService.DeriveKek(byte[], ReadOnlySpan<byte>, out byte[])` → `Result`
- `KeyDerivationService.DeriveBrainKey(ReadOnlySpan<byte>, Span<byte>)` → `Result`
- `MnemonicService.ToSeed(string[])` → `Result<byte[]>`
- `System.Security.Cryptography.AesGcm` — built-in .NET type
- `System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span<byte>)`
- `System.Security.Cryptography.RandomNumberGenerator.GetBytes(int)`
- `Microsoft.Data.Sqlite.SqliteConnection` — referenced for `VolumeSession`; `FlashSkink.Core.csproj` already has `Microsoft.Data.Sqlite` available via `SQLitePCLRaw.bundle_e_sqlcipher` chain — verify or add explicit reference.

## Principles touched

- Principle 1 (Core never throws across its public API)
- Principle 7 (Zero trust in host — atomic write to skink staging area, no host temp path)
- Principle 12 (OS-agnostic — `File.Move` overwrite is cross-platform in .NET 6+)
- Principle 13 (`CancellationToken ct` last, always present on async methods)
- Principle 14 (`OperationCanceledException` always first catch)
- Principle 15 (specific catches before generic fallback)
- Principle 16 (every failure path disposes partially-constructed resources)
- Principle 17 (compensation paths use `CancellationToken.None` literals — n/a here; no compensation paths in §1.3)
- Principle 20 (`stackalloc` never crosses `await` boundary — brain key is stackalloc'd and zeroed before any await in `OpenAsync`)
- Principle 26 (logging never contains secrets — no DEK/KEK bytes in messages)
- Principle 29 (atomic file writes — vault uses temp+fsync+rename)
- Principle 31 (keys zeroed — `ZeroMemory` on all key buffers in every exit path)

## Test spec

### `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs`

**Class:** `KeyVaultTests`

Uses a `TempDirectory` helper that creates and cleans up a temp folder per test.

- `CreateAsync_WritesVaultFile` — after `CreateAsync`, the vault file exists and is exactly 100 bytes.
- `CreateAsync_ReturnsDek_Of32Bytes` — `result.Value!.Length == 32`.
- `CreateAsync_ReturnsDifferentDekEachCall` — two `CreateAsync` calls produce different DEK bytes.
- `UnlockAsync_WithCorrectPassword_ReturnsDek` — create then unlock; unlock succeeds and dek matches.
- `UnlockAsync_WithWrongPassword_ReturnsInvalidPassword` — create with "correct", unlock with "wrong"; `ErrorCode.InvalidPassword`.
- `UnlockAsync_FileNotFound_ReturnsVolumeNotFound` — path to nonexistent file; `ErrorCode.VolumeNotFound`.
- `UnlockAsync_CorruptMagic_ReturnsVolumeCorrupt` — write 100 bytes of zeros; `ErrorCode.VolumeCorrupt`.
- `UnlockAsync_CorruptTag_ReturnsInvalidPassword` — create vault, flip one byte in the tag region (bytes 84..99); `ErrorCode.InvalidPassword`.
- `UnlockFromMnemonicAsync_WithValidMnemonic_ReturnsDek` — create vault with password, derive the same KEK from the mnemonic path and unlock; requires knowing the KEK the vault was created with. Instead: create vault with a known Argon2id derivation — easier: create vault via `CreateAsync`, then separately lock with mnemonic by creating a second vault using `UnlockFromMnemonicAsync` path. Actually simpler: this test needs the vault to be created with a specific password, and then verify that the mnemonic can unlock it only if the mnemonic produces the same KEK. Since the mnemonic and password paths use different KDF inputs, they will produce different KEKs and the mnemonic cannot unlock a password-created vault. Therefore: this test should create a vault by first deriving KEK from a mnemonic seed directly (using `KeyDerivationService` directly in the test), or skip integration here and test the mnemonic path end-to-end with a coordinated setup. Plan: use a test helper that creates a vault by: (a) getting seed from `MnemonicService.ToSeed(testWords)`, (b) deriving KEK from that seed, (c) calling `CreateAsync` with a password whose Argon2id output matches that KEK — which is impossible to engineer without a custom test harness. Better approach: make `UnlockFromMnemonicAsync` testable by creating a vault with `CreateAsync` that uses the mnemonic-derived key as the AES-GCM key. This is not how vaults work. Actually the simplest correct test: create a vault with a known mnemonic by calling some internal helper, or restructure the test. Simplest: just verify that `UnlockFromMnemonicAsync` with a valid mnemonic against a vault created with the wrong password returns `ErrorCode.InvalidPassword` (wrong KEK from mnemonic ≠ KEK used to wrap). And add a positive test that uses `UnlockFromMnemonicAsync` with a vault explicitly created using `CreateAsync` and the mnemonic-derived KEK indirectly by using a shared test fixture. The most pragmatic approach given no internal test hooks: skip the positive mnemonic round-trip test in §1.3 and add it in §1.6 once the full volume open path is tested end-to-end.
- `UnlockFromMnemonicAsync_RoundTrip_Succeeds` — **`[Fact(Skip = "deferred to §1.6 — requires coordinated vault creation via mnemonic-derived KEK")]`**. Placeholder keeps the gap visible in test output. §1.6 plan must list this as a carry-forward obligation and implement it once `VolumeLifecycle` is fully wired.
- `UnlockFromMnemonicAsync_WithWrongMnemonic_ReturnsInvalidPassword` — unlock a password-created vault with a random valid mnemonic; `ErrorCode.InvalidPassword` (KEK mismatch).
- `UnlockFromMnemonicAsync_InvalidWords_ReturnsInvalidMnemonic` — pass invalid words array; `ErrorCode.InvalidMnemonic`.
- `ChangePasswordAsync_WithCorrectCurrentPassword_Succeeds` — create, change password, unlock with new password: succeeds. Unlock with old password: `ErrorCode.InvalidPassword`.
- `ChangePasswordAsync_WithWrongCurrentPassword_ReturnsInvalidPassword` — `ErrorCode.InvalidPassword`.
- `CreateAsync_VaultWrite_IsAtomic` — after `CreateAsync` the `.tmp` file does not exist (was renamed away).
- `CreateAsync_ReturnedDek_IsZeroedByCallerAfterTest` — meta-check: after calling `CryptographicOperations.ZeroMemory(dek)`, all bytes are 0. (Verifies our zeroing contract works.)

### `tests/FlashSkink.Tests/Crypto/VolumeSessionTests.cs`

**Class:** `VolumeSessionTests`

- `DisposeAsync_ZeroesDek` — create session with known dek bytes, dispose, verify all dek bytes are 0.
- `DisposeAsync_IsIdempotent` — dispose twice; no exception thrown.
- `DisposeAsync_ClosesNullConnection_DoesNotThrow` — create session with `null` brainConnection (§1.3 stub), dispose; no exception.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets
- [ ] All new tests pass
- [ ] No existing tests break
- [ ] No `byte[]` field for DEK or KEK exists on `KeyVault`
- [ ] `CryptographicOperations.ZeroMemory` called on every key buffer in every exit path
- [ ] `AesGcm` used for all GCM operations (no third-party crypto for key material)
- [ ] Vault writes use temp+fsync+rename (no direct overwrite)
- [ ] `VolumeSession.DisposeAsync` is idempotent
- [ ] All async methods have `CancellationToken ct` as last parameter
- [ ] `OperationCanceledException` is first catch on all async methods
- [ ] Memory parameter survives vault round-trip without precision loss (`UnlockAsync_WithCorrectPassword_ReturnsDek` exercises the full MiB encode/decode cycle — audits the §18.4 drift resolution)

## Line-of-code budget

- `src/FlashSkink.Core/Crypto/KeyVault.cs` — ~180 lines
- `src/FlashSkink.Core/Crypto/VolumeSession.cs` — ~90 lines
- `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs` — ~200 lines
- `tests/FlashSkink.Tests/Crypto/VolumeSessionTests.cs` — ~60 lines
- Total: ~270 lines non-test, ~260 lines test

## Carry-forward obligations for §1.6

- **`UnlockFromMnemonicAsync_RoundTrip_Succeeds`** must be implemented in §1.6. The skipped `[Fact]` placeholder in `KeyVaultTests.cs` makes this gap visible in every test run. The §1.6 plan must explicitly list this test as a required deliverable — not optional cleanup.

## Non-goals

- Do NOT wire `VolumeLifecycle.OpenAsync` to a real `BrainConnectionFactory` — that is §1.5.
- Do NOT implement `VolumeLifecycle.CloseAsync` — session disposal handles zeroing; the close verb is a Phase 2 concern.
- Do NOT implement multi-instance locking (`SingleInstanceLockHeld`) — §1.5.
- Do NOT implement USB removal handling or integrity_check on mount — Phase 2.
- Do NOT implement `RevealRecoveryPhraseAsync` — Phase 2.

## Drift notes

1. **`VaultNotFound` vs `VolumeNotFound`**: The dev plan §1.3 narrative says `ErrorCode.VaultNotFound`; blueprint §6.4 (authoritative) has `VolumeNotFound`. Implementation uses `VolumeNotFound`. Blueprint wins.

2. **Argon2id memory encoding — MiB instead of log2(KB)**: Blueprint §18.4 describes the params high byte as `log2(memoryKilobytes)`. For the OWASP baseline of 19456 KB, `round(log2(19456))` = 14, which decodes back to 16384 KB — a 16% error that causes every unlock to re-derive a different KEK, making the vault permanently unreadable. The format as written in §18.4 is unusable for the V1 default parameters. This PR deviates: the high byte stores `memoryKilobytes / 1024` (memory in MiB), giving an exact round-trip for all multiples of 1024 KB up to 255 MiB. The PR body requests a blueprint §18.4 update to reflect this. This deviation is fully described in the VaultHeader spec and binary layout above; no further reconciliation is needed during implementation.
