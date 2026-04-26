# PR 1.2 — Mnemonic service and key derivation

**Branch:** pr/1.2-mnemonic-and-key-derivation
**Blueprint sections:** §18.1, §18.2, §18.3
**Dev plan section:** phase-1 §1.2

## Scope

Delivers two services in `FlashSkink.Core/Crypto/`: `MnemonicService` (BIP-39 generate /
validate / seed-derive) and `KeyDerivationService` (Argon2id KEK derivation, HKDF brain-key
derivation). The BIP-39 English wordlist is embedded as a compile-time resource — no
third-party BIP-39 library is used despite `dotnetstandard-bip39` being pinned in
`Directory.Packages.props` (that pin is Phase 0 scaffolding; this PR intentionally omits it).
`Konscious.Security.Cryptography.Argon2` (already pinned at 1.3.1) is added as a project
reference to Core.

## Files to create

- `src/FlashSkink.Core/Resources/bip39-english.txt` — 2048-word BIP-39 English wordlist, one word per line (embedded resource), 2048 lines
- `src/FlashSkink.Core/Crypto/MnemonicService.cs` — `MnemonicService` sealed class, ~130 lines
- `src/FlashSkink.Core/Crypto/KeyDerivationService.cs` — `KeyDerivationService` sealed class, ~90 lines
- `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs` — unit tests, ~170 lines
- `tests/FlashSkink.Tests/Crypto/KeyDerivationServiceTests.cs` — unit tests, ~110 lines

## Files to modify

- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `PackageReference` for `Konscious.Security.Cryptography.Argon2`; add `EmbeddedResource` item for the wordlist
- `src/FlashSkink.Core/Placeholder.cs` — delete (Core now has real content)

## Dependencies

- NuGet: `Konscious.Security.Cryptography.Argon2` 1.3.1 (already in `Directory.Packages.props`; add `PackageReference` in Core csproj only)
- Project references: none new

## Public API surface

### `FlashSkink.Core.Crypto.MnemonicService` (sealed class)
Summary intent: Owns the BIP-39 mnemonic lifecycle — generation, validation, and seed
derivation. Never persists or logs mnemonic words.

- `Result<string[]> Generate()`
  Produces a 24-word mnemonic from 32 bytes of `RandomNumberGenerator.GetBytes`.
  Returns the word array; the caller is responsible for zeroing/clearing it after display.
  Fails with `ErrorCode.CryptoFailed` only if the underlying RNG or SHA-256 throws.

  Wait — `CryptoFailed` is not in the blueprint §6.4 enum (see §1.1 drift note). Use
  `ErrorCode.EncryptionFailed` for any internal crypto-library failure in this service.

- `Result Validate(string[] words)`
  Validates that all 24 words exist in the English BIP-39 wordlist and that the embedded
  checksum is correct. Returns `ErrorCode.InvalidMnemonic` on any validation failure.
  Never throws.

- `Result<byte[]> ToSeed(string[] words)`
  Applies BIP-39 seed derivation: PBKDF2-HMAC-SHA512, password = words joined with `" "`,
  salt = `"mnemonic"` (UTF-8, per BIP-39 spec; empty passphrase), 2048 iterations,
  64-byte output. Returns the 64-byte seed. Caller must zero after use.
  Returns `ErrorCode.InvalidMnemonic` if `Validate` would fail on the input words.
  Returns `ErrorCode.EncryptionFailed` on internal crypto failure.

---

### `FlashSkink.Core.Crypto.KeyDerivationService` (sealed class)
Summary intent: Owns the cryptographic KDF layer — Argon2id KEK derivation and HKDF
brain-key derivation. Caller controls all output buffer lifetimes.

- `Result DeriveKek(byte[] seed, ReadOnlySpan<byte> argon2Salt, out byte[] kek)`
  Runs Argon2id (m=19456 KB, t=2, p=1) with `seed` as the password and `argon2Salt` as the
  salt. Writes a 32-byte KEK to `kek`. `seed` is the 64-byte BIP-39 seed from
  `MnemonicService.ToSeed`; caller must zero it after this call.
  On failure: `kek = Array.Empty<byte>()`, returns `ErrorCode.KeyDerivationFailed`.

- `Result DeriveKekFromPassword(ReadOnlySpan<byte> passwordBytes, ReadOnlySpan<byte> argon2Salt, out byte[] kek)`
  Same Argon2id parameters. `passwordBytes` is the raw user-password buffer — accepting
  `ReadOnlySpan<byte>` so the caller can pass a stack-pinned or pooled buffer and zero it
  after the call without the service retaining a reference. Internally copies to `byte[]`
  only for the duration of the Argon2id call.
  On failure: `kek = Array.Empty<byte>()`, returns `ErrorCode.KeyDerivationFailed`.

- `Result DeriveBrainKey(ReadOnlySpan<byte> dek, Span<byte> destination)`
  Writes HKDF-SHA256(dek, salt=null, info=`"brain"u8`) into `destination`. `destination`
  must be exactly 32 bytes. Caller zeroes `destination` after use (typically
  `CryptographicOperations.ZeroMemory`).
  Returns `ErrorCode.KeyDerivationFailed` on failure.

## Internal types

None — `MnemonicService` uses a `private static readonly FrozenSet<string> _wordlist` loaded
once from the embedded resource. No additional internal types are introduced.

## Method-body contracts

### `MnemonicService` wordlist loading
- Loaded lazily via `static readonly` initialiser (not a `Lazy<T>` — the static field
  initialiser guarantees single initialisation under the CLR lock).
- Resource path: `FlashSkink.Core.Resources.bip39-english.txt` (manifest resource name
  derived from the file path with directory separators replaced by `.`).
- Loaded via `Assembly.GetExecutingAssembly().GetManifestResourceStream(...)`, split on
  newlines, trimmed, and stored as `FrozenSet<string>` for O(1) lookup.

### `MnemonicService.Generate` — BIP-39 encoding
1. `RandomNumberGenerator.GetBytes(32)` → 32-byte entropy.
2. SHA-256(entropy) → hash; checksum = hash[0] (first 8 bits, since 256-bit entropy requires
   8-bit checksum per BIP-39 spec: CS = ENT / 32).
3. Concatenate entropy bytes + checksum byte → 33 bytes = 264 bits = 24 × 11 bits.
4. For i = 0..23: extract 11-bit index = bits[i×11 .. i×11+10]; look up `_wordlistArray[index]`.
5. Return the 24-word array.
- The wordlist must also be held as a `private static readonly string[]` (ordered, for index
  lookup by position). `FrozenSet` is for O(1) membership test; the ordered array is for
  index-to-word mapping.

### `MnemonicService.Validate` — BIP-39 decoding and checksum check
1. Guard: `words.Length != 24` → `ErrorCode.InvalidMnemonic`.
2. For each word, look up index in `_wordlistArray` (sequential scan or reverse-map dictionary).
   Any unknown word → `ErrorCode.InvalidMnemonic`.
3. Reconstruct 264-bit bitstring from 24 × 11-bit indices.
4. Extract entropy = first 256 bits (32 bytes); extract checksum_bits = last 8 bits.
5. SHA-256(entropy) → expected_checksum = hash[0].
6. `checksum_bits != expected_checksum` → `ErrorCode.InvalidMnemonic`.
7. Success → `Result.Ok()`.
- For efficient reverse lookup, add a second static field:
  `private static readonly IReadOnlyDictionary<string, int> _wordIndex` mapping each word to
  its 0-based index, initialised alongside `_wordlist`.

### `MnemonicService.ToSeed`
1. Call `Validate(words)` internally; propagate failure.
2. Join words with `" "` → mnemonic string (UTF-8 NFKD — use `string.Normalize(NormalizationForm.FormKD)`).
3. Encode as UTF-8 bytes → `passwordBytes`.
4. Salt = `Encoding.UTF8.GetBytes("mnemonic")` (BIP-39 spec, empty passphrase).
5. `Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 2048, HashAlgorithmName.SHA512, 64)`.
6. Return `Result<byte[]>.Ok(seed)`. Caller must zero.

### `KeyDerivationService.DeriveKek`
1. Construct `Argon2id(seed)` with `MemorySize = 19456`, `Iterations = 2`,
   `DegreeOfParallelism = 1`, `Salt = argon2Salt.ToArray()`.
2. `kek = argon2.GetBytes(32)`.
3. On `OutOfMemoryException` or any other exception: `kek = Array.Empty<byte>()`,
   return `ErrorCode.KeyDerivationFailed`.

### `KeyDerivationService.DeriveKekFromPassword`
1. Copy `passwordBytes` to a `byte[]` temp (required by Konscious API).
2. Same Argon2id construction and call as `DeriveKek`.
3. Zero `temp` in a `finally` block (never leave the password copy alive after the call).
4. On failure: `kek = Array.Empty<byte>()`, return `ErrorCode.KeyDerivationFailed`.

### `KeyDerivationService.DeriveBrainKey`
1. Guard: `destination.Length != 32` → `ErrorCode.KeyDerivationFailed` with message.
2. `HKDF.DeriveKey(HashAlgorithmName.SHA256, dek.ToArray(), destination, salt: ReadOnlySpan<byte>.Empty, info: "brain"u8)`.
   Note: `System.Security.Cryptography.HKDF` (built into .NET 5+) accepts Span outputs.
3. Return `Result.Ok()`.

## Integration points

- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorContext`, `ErrorCode` —
  from PR 1.1. Signatures: `Result.Ok()`, `Result.Fail(ErrorCode, string, Exception?)`,
  `Result<T>.Ok(T)`, `Result<T>.Fail(ErrorCode, string, Exception?)`.
- `System.Security.Cryptography.RandomNumberGenerator.GetBytes(int)` — .NET built-in.
- `System.Security.Cryptography.SHA256.HashData(ReadOnlySpan<byte>)` — .NET built-in.
- `System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(...)` — .NET built-in.
- `System.Security.Cryptography.HKDF.DeriveKey(...)` — .NET built-in.
- `Konscious.Security.Cryptography.Argon2id` — NuGet package (v1.3.1).

## Principles touched

- Principle 1 (Core never throws across its public API — all methods return `Result`/`Result<T>`)
- Principle 8 (Core holds no UI framework reference)
- Principle 12 (OS-agnostic — embedded resource, standard .NET crypto, no platform APIs)
- Principle 13 (`CancellationToken ct` last — n/a here: all methods are synchronous CPU-bound,
  no async I/O; the synchronous constraint is imposed by the `out` parameter pattern which is
  incompatible with async)
- Principle 14 (`OperationCanceledException` first — n/a: synchronous methods)
- Principle 15 (bare `catch (Exception)` never the only catch — specific catches first)
- Principle 26 (logging never contains secrets — `MnemonicService` never logs word arrays;
  `KeyDerivationService` never logs key bytes)
- Principle 27 (Core logs internally; callers log the Result — the services log at the
  `Result.Fail` construction site)
- Principle 28 (Core depends only on MEL abstractions — no Serilog in Core)
- Principle 31 (keys zeroed on volume close — `DeriveKekFromPassword` zeroes its internal
  password copy in `finally`; caller retains obligation for the returned `kek` and `seed`)

## Test spec

### `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs`

**Class:** `MnemonicServiceTests`

- `Generate_Returns24Words` — `Generate().Value` has length 24.
- `Generate_AllWordsAreInBip39Wordlist` — every word in the result is a valid BIP-39 English
  word (load the wordlist from the assembly manifest resource and check membership).
- `Generate_TwoCallsProduce_DifferentMnemonics` — two successive `Generate()` calls return
  different word arrays (collision probability negligible at 256-bit entropy).
- `Generate_Succeeds` — `Generate().Success` is true.
- `Validate_WithGeneratedMnemonic_Succeeds` — round-trip: generate then validate.
- `Validate_WrongWordCount_ReturnsInvalidMnemonic` — pass 23 words; assert
  `result.Error!.Code == ErrorCode.InvalidMnemonic`.
- `Validate_UnknownWord_ReturnsInvalidMnemonic` — replace word[0] with "xyzzy"; assert
  `ErrorCode.InvalidMnemonic`.
- `Validate_SwappedWords_BadChecksum_ReturnsInvalidMnemonic` — swap word[0] and word[1] in
  a generated mnemonic to corrupt the checksum; assert `ErrorCode.InvalidMnemonic`.
  (Note: word swap may or may not corrupt checksum depending on values — use a fixed test
  mnemonic derived from all-zeros entropy where the expected result is known.)
- `Validate_NullWords_ReturnsInvalidMnemonic` — pass null element; assert InvalidMnemonic.
  (Guard: treat null word as unknown word.)
- `ToSeed_Returns64Bytes` — `ToSeed(validWords).Value!.Length == 64`.
- `ToSeed_IsDeterministic` — same words, two calls, same seed bytes.
- `ToSeed_DifferentMnemonics_ProduceDifferentSeeds` — two different generated mnemonics
  produce different seeds.
- `ToSeed_Bip39AllZerosVector_ProducesKnownSeed` — `[Theory]` with the standard BIP-39
  all-zeros test vector (256-bit entropy = 32 zero bytes → 24 words including "art" as last
  word). Assert seed first 8 bytes match the expected hex from the BIP-39 spec. The exact
  expected seed for the all-zeros mnemonic with empty passphrase is:
  `5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc1` (first 32 bytes).
  Assert using `SequenceEqual` on the first 8 bytes only for brevity; note full 64-byte
  expected is in the BIP-39 test-vectors repo.
- `ToSeed_InvalidWords_ReturnsInvalidMnemonic` — pass invalid words; assert failure
  propagated from internal Validate.

### `tests/FlashSkink.Tests/Crypto/KeyDerivationServiceTests.cs`

**Class:** `KeyDerivationServiceTests`

- `DeriveKek_ProducesKek_Of32Bytes` — `DeriveKek(seed, salt, out kek)` succeeds;
  `kek.Length == 32`.
- `DeriveKek_IsDeterministic` — same seed + salt → same kek bytes.
- `DeriveKek_DifferentSalt_ProducesDifferentKek` — same seed, different 32-byte salts →
  different kek.
- `DeriveKek_DifferentSeed_ProducesDifferentKek` — different seeds, same salt → different
  kek.
- `DeriveKekFromPassword_ProducesKek_Of32Bytes` — same length assertion via password path.
- `DeriveKekFromPassword_IsDeterministic` — same password bytes + salt → same kek.
- `DeriveKekFromPassword_DifferentPassword_DifferentKek` — different passwords → different
  kek.
- `DeriveKekFromPassword_And_DeriveKek_WithSameBytesAndSalt_ProduceSameKek` — feed the same
  32-byte value as both `seed` and `passwordBytes` with the same salt; both overloads must
  produce identical output (they share the same Argon2id parameters).
- `DeriveBrainKey_ProducesExactly32Bytes` — `destination = new byte[32]`; assert Success and
  non-zero content.
- `DeriveBrainKey_IsDeterministic` — same DEK → same brain key.
- `DeriveBrainKey_DifferentDek_ProducesDifferentKey` — different DEK → different brain key.
- `DeriveBrainKey_WrongDestinationLength_ReturnsKeyDerivationFailed` — pass a 16-byte
  destination span; assert `ErrorCode.KeyDerivationFailed`.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (`dotnet build --warnaserror`)
- [ ] All new tests pass (`dotnet test`)
- [ ] No existing tests break
- [ ] `src/FlashSkink.Core/Placeholder.cs` removed
- [ ] `bip39-english.txt` is embedded (verify `EmbeddedResource` in csproj and
  `GetManifestResourceStream` in `MnemonicService` resolves non-null)
- [ ] `dotnetstandard-bip39` package is NOT referenced anywhere in Core
- [ ] No mnemonic words or key bytes appear in any log call or `ErrorContext.Message`
- [ ] `DeriveKekFromPassword` zeroes its internal password copy in `finally`
- [ ] All public methods return `Result` or `Result<T>`

## Line-of-code budget

- `src/FlashSkink.Core/Resources/bip39-english.txt` — 2048 lines (data only)
- `src/FlashSkink.Core/Crypto/MnemonicService.cs` — ~130 lines
- `src/FlashSkink.Core/Crypto/KeyDerivationService.cs` — ~90 lines
- `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs` — ~170 lines
- `tests/FlashSkink.Tests/Crypto/KeyDerivationServiceTests.cs` — ~110 lines
- Total: ~220 lines non-test, ~280 lines test (excluding wordlist)

## Non-goals

- Do NOT use `dotnetstandard-bip39` or any third-party BIP-39 library; embed the wordlist.
- Do NOT implement `KeyVault` (DEK wrap/unwrap) — that is PR 1.3.
- Do NOT implement `VolumeLifecycle` — PR 1.3.
- Do NOT expose an async Argon2id path — the synchronous `out byte[]` pattern is deliberate
  (CPU-bound, ~100 ms, and incompatible with `out` parameters in async methods).
- Do NOT add BYOC provider credential encryption — that uses a different KDF path (PR 1.3+).
- Do NOT zeroize the returned `kek` or `seed` buffers in these services — the caller controls
  lifetime (documented on each method).

## Note on `CryptoFailed` vs blueprint enum

The dev plan §1.2 does not name specific `ErrorCode` values for this PR. PR 1.1 established
the full enum from blueprint §6.4. Failures in `MnemonicService` and `KeyDerivationService`
map to: `ErrorCode.InvalidMnemonic` (validation failures) and `ErrorCode.KeyDerivationFailed`
(KDF failures). Both are present in the blueprint §6.4 enum.
