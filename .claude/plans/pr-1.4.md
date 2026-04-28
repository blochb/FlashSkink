# PR 1.4 — Crypto pipeline and blob header

**Branch:** pr/1.4-crypto-pipeline-and-blob-header
**Blueprint sections:** §13.6, §14.1 (steps 2–4), §14.2 (step 4), §18.1
**Dev plan section:** phase-1 §1.4

## Scope

Delivers two types in `FlashSkink.Core/Crypto/`: `BlobHeader` (serialise/parse the 20-byte
on-disk preamble and expose format constants) and `CryptoPipeline` (AES-256-GCM encrypt/decrypt
over caller-supplied buffers). A supporting `BlobFlags` enum is created alongside them.
`CryptoPipeline` is a pure byte-in/byte-out transform; it has zero knowledge of files, blobs,
repositories, or vault keys.

## Files to create

- `src/FlashSkink.Core/Crypto/BlobFlags.cs` — `[Flags] enum BlobFlags : ushort`, ~15 lines
- `src/FlashSkink.Core/Crypto/BlobHeader.cs` — `static class BlobHeader` with constants and
  Write/Parse, ~80 lines
- `src/FlashSkink.Core/Crypto/CryptoPipeline.cs` — `sealed class CryptoPipeline` with Encrypt
  and Decrypt, ~120 lines
- `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` — round-trip, tamper-detection,
  header-parse, and error-path tests, ~220 lines

## Files to modify

None.

## Dependencies

- NuGet: none new (`AesGcm` is `System.Security.Cryptography`; `IMemoryOwner<byte>` and
  `ArrayPool<byte>` are `System.Buffers`; both are in the BCL for `net10.0`).
- Project references: none new.

## Drift note — ErrorCode names

The dev plan §1.4 names `VaultCorrupt`, `VaultVersionUnsupported`, and `CryptoFailed`.
None of these exist in the `ErrorCode` enum established by PR 1.1. This PR uses the
closest existing codes and does **not** add new enum members (out of scope for 1.4):

| Dev-plan name         | Actual code used           | Reason                                    |
|-----------------------|----------------------------|-------------------------------------------|
| `VaultCorrupt`        | `VolumeCorrupt`            | Blob magic invalid → on-disk structure corrupt |
| `VaultVersionUnsupported` | `VolumeIncompatibleVersion` | Unknown blob version                  |
| `CryptoFailed`        | `DecryptionFailed`         | GCM authentication tag mismatch          |

## Public API surface

### `FlashSkink.Core.Crypto.BlobFlags` ([Flags] enum : ushort)

Summary intent: bitmask recording which optional pipeline stages were applied to a blob
(compression algorithm). Bit positions match §13.6.

- `None = 0`
- `CompressedLz4 = 1 << 0`
- `CompressedZstd = 1 << 1`

### `FlashSkink.Core.Crypto.BlobHeader` (static class)

Summary intent: serialises and parses the 20-byte versioned preamble that precedes every
ciphertext blob on disk. Centralises all format constants so no other type hard-codes sizes.

**Constants (all `public const int`):**
- `MagicSize = 4` — byte length of the magic marker `"FSBL"`.
- `NonceSize = 12` — AES-GCM nonce length in bytes.
- `HeaderSize = 20` — total on-disk header size (magic + version + flags + nonce).
- `TagSize = 16` — AES-256-GCM authentication tag length in bytes.

**Methods:**
- `internal static void Write(Span<byte> destination, BlobFlags flags, ReadOnlySpan<byte> nonce)` —
  writes exactly `HeaderSize` bytes to `destination[..HeaderSize]`. Writes magic `"FSBL"` as
  ASCII, version `1` as little-endian `uint16`, flags as little-endian `uint16`, then the
  12-byte nonce. Precondition: `destination.Length >= HeaderSize`, `nonce.Length == NonceSize`.
  Throws `ArgumentException` on violated preconditions — `Write` is `internal` so this remains
  a programming-error throw rather than a `Result.Fail`, satisfying Principle 1 (public
  boundary only). Only `CryptoPipeline.Encrypt` calls it and always satisfies the invariant.

- `static Result Parse(ReadOnlySpan<byte> source, out BlobFlags flags, out ReadOnlySpan<byte> nonce)` —
  validates `source.Length >= HeaderSize`, checks magic bytes, checks version == 1, sets
  `flags` from the flags field, sets `nonce` to `source.Slice(8, NonceSize)` (a zero-copy view
  into `source`). Returns `Result.Fail(ErrorCode.VolumeCorrupt, ...)` on wrong magic or
  insufficient length. Returns `Result.Fail(ErrorCode.VolumeIncompatibleVersion, ...)` on
  unknown version (anything != 1). On success `flags` and `nonce` are set; on failure both
  are set to their default values (`BlobFlags.None` and `ReadOnlySpan<byte>.Empty`).
  **Nonce lifetime:** the returned `nonce` span is valid only as long as `source` is in scope;
  callers must not store it beyond the lifetime of `source`.

### `FlashSkink.Core.Crypto.CryptoPipeline` (sealed class)

Summary intent: stateless AES-256-GCM encrypt/decrypt over caller-supplied memory. Generates
a fresh random nonce per encryption. Writes blob header and ciphertext into the output buffer
provided by the caller. The caller owns all buffer lifetimes.

**Constructor:** parameterless. No DI dependencies.

- `Result Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad, IMemoryOwner<byte> outputOwner, out int bytesWritten)` —
  Generates a 12-byte nonce via `stackalloc byte[NonceSize]` filled by
  `RandomNumberGenerator.Fill`. Calls `BlobHeader.Write` to write the header.
  Calls `AesGcm.Encrypt` to write ciphertext and tag into the output span.
  Output layout: `[Header (20B)] || [Ciphertext (plaintext.Length B)] || [Tag (16B)]`.
  Sets `bytesWritten = HeaderSize + plaintext.Length + TagSize` on success; 0 on failure.
  Returns `Result.Fail(ErrorCode.EncryptionFailed, ...)` on `CryptographicException`.
  Returns `Result.Fail(ErrorCode.Unknown, ...)` on any other exception.
  Precondition: `dek.Length == 32`, `outputOwner.Memory.Length >= HeaderSize + plaintext.Length + TagSize`.
  Precondition violations → `Result.Fail(ErrorCode.Unknown, ...)` with a descriptive message
  (not a throw, to honour Principle 1 — Encrypt is public).

- `Result Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad, IMemoryOwner<byte> outputOwner, out int bytesWritten)` —
  Parses the header via `BlobHeader.Parse` (propagates its error on failure).
  Extracts ciphertext span: `blob[HeaderSize..(blob.Length - TagSize)]`.
  Extracts tag span: `blob[(blob.Length - TagSize)..]`.
  Calls `AesGcm.Decrypt` writing plaintext to `outputOwner.Memory.Span[..ciphertextLength]`.
  Sets `bytesWritten = ciphertextLength` on success; 0 on failure.
  Returns `Result.Fail(ErrorCode.DecryptionFailed, ...)` on `CryptographicException` (GCM
  authentication tag mismatch).
  Returns `Result.Fail(ErrorCode.Unknown, ...)` on any other exception.
  Precondition: `dek.Length == 32`, `blob.Length >= HeaderSize + TagSize` (zero-byte
  ciphertext is valid — AES-GCM handles it at the BCL level),
  `outputOwner.Memory.Length >= blob.Length - HeaderSize - TagSize`.
  Precondition violations → `Result.Fail(ErrorCode.Unknown, ...)`.

## Internal types

None.

## Method-body contracts

### `BlobHeader.Parse`
- `source.Length < HeaderSize` → `ErrorCode.VolumeCorrupt`.
- `source[..4] != "FSBL"u8` → `ErrorCode.VolumeCorrupt`.
- `BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]) != 1` → `ErrorCode.VolumeIncompatibleVersion`.
- Success path: `flags = (BlobFlags)BinaryPrimitives.ReadUInt16LittleEndian(source[6..8])`,
  `nonce = source.Slice(8, NonceSize)`.
- No `try/catch` needed — all operations are on spans, no exceptions possible.

### `CryptoPipeline.Encrypt`
- Precondition guard before any allocation: if `dek.Length != 32` or
  `outputOwner.Memory.Length < HeaderSize + plaintext.Length + TagSize`, return
  `Result.Fail(ErrorCode.Unknown, "<descriptive message>")` immediately, `bytesWritten = 0`.
- Exact BCL call: `aes.Encrypt(nonce, plaintext, ciphertext, tag, aad)` where:
  - `nonce` — `stackalloc byte[NonceSize]` filled by `RandomNumberGenerator.Fill`
  - `ciphertext` — `outputOwner.Memory.Span[HeaderSize..(HeaderSize + plaintext.Length)]`
  - `tag` — `outputOwner.Memory.Span[(HeaderSize + plaintext.Length)..(HeaderSize + plaintext.Length + TagSize)]`
- `BlobHeader.Write` is called with the stackalloc nonce **before** `AesGcm.Encrypt`, so the
  header is always present if the encrypt call throws.
- Catch ordering:
  1. `catch (CryptographicException ex)` → `Result.Fail(ErrorCode.EncryptionFailed, "AES-GCM encryption failed.", ex)`
  2. `catch (Exception ex)` → `Result.Fail(ErrorCode.Unknown, "Unexpected error during encryption.", ex)`
- `AesGcm` is disposed via `using` regardless of success or failure (Principle 16).
- Nonce bytes are `stackalloc`, filled synchronously, never allocated on the heap (Principle 20).
- Key bytes in `dek` are never logged or placed in `ErrorContext.Metadata`.

### `CryptoPipeline.Decrypt`
- Precondition guard: if `dek.Length != 32` or `blob.Length < HeaderSize + TagSize`, return
  `Result.Fail(ErrorCode.Unknown, "<descriptive message>")`, `bytesWritten = 0`.
- `BlobHeader.Parse` failure → propagate with `return parseResult` immediately (before any
  `AesGcm` allocation).
- Precondition guard for output buffer: if `outputOwner.Memory.Length < ciphertextLength`, return
  `Result.Fail(ErrorCode.Unknown, ...)`, `bytesWritten = 0`.
- Catch ordering:
  1. `catch (CryptographicException ex)` → `Result.Fail(ErrorCode.DecryptionFailed, "AES-GCM authentication failed — blob may be tampered.", ex)`
  2. `catch (Exception ex)` → `Result.Fail(ErrorCode.Unknown, "Unexpected error during decryption.", ex)`
- `AesGcm` is disposed via `using` regardless of success or failure (Principle 16).

## Integration points

- `FlashSkink.Core.Abstractions.Results.Result` — `Result.Ok()`, `Result.Fail(ErrorCode, string, Exception?)` from PR 1.1.
- `FlashSkink.Core.Abstractions.Results.ErrorCode` — `VolumeCorrupt`, `VolumeIncompatibleVersion`, `EncryptionFailed`, `DecryptionFailed`, `Unknown` from PR 1.1.
- `System.Security.Cryptography.AesGcm` — BCL, net10.0. Constructor: `new AesGcm(ReadOnlySpan<byte> key, int tagSizeInBytes)`.
- `System.Security.Cryptography.RandomNumberGenerator` — `RandomNumberGenerator.Fill(Span<byte>)`.
- `System.Buffers.IMemoryOwner<byte>` — BCL. Caller provides from `ArrayPool<byte>.Shared` (not inside CryptoPipeline).
- `System.Buffers.Binary.BinaryPrimitives` — `ReadUInt16LittleEndian`, `WriteUInt16LittleEndian` for header fields.

## Principles touched

- Principle 1 (Core never throws across its public API — all public methods return `Result`)
- Principle 8 (Core holds no UI framework reference — trivially satisfied)
- Principle 12 (OS-agnostic — BCL AES-GCM is platform-independent)
- Principle 15 (No bare `catch (Exception ex)` as the only catch — `CryptographicException` caught first)
- Principle 16 (Every failure path disposes — `AesGcm` always in `using`)
- Principle 18 (Allocation-conscious hot path — nonce via `stackalloc`, output into caller buffer; `new AesGcm(...)` inside `using` is a small unavoidable BCL heap allocation — acceptable)
- Principle 19 (Buffer ownership via `IMemoryOwner<byte>` — caller provides, pipeline writes)
- Principle 20 (`stackalloc` never crosses `await` — methods are synchronous, trivially satisfied)
- Principle 26 (Logging never contains secrets — DEK bytes never logged or in `ErrorContext.Metadata`)
- Principle 27 (Core logs internally — no logger needed; CryptoPipeline constructs Result.Fail; the caller — WritePipeline — logs the returned Result. Pattern matches KeyDerivationService.)

## Test spec

### `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs`

**Class: `BlobHeaderTests`**

- `Write_ThenParse_RoundTrips_FlagsAndNonce` — writes header with `BlobFlags.CompressedLz4`
  and a known 12-byte nonce, parses, asserts flags and nonce bytes match.
- `Parse_WithBadMagic_ReturnsVolumeCorrupt` — source with wrong magic bytes; assert
  `Result.Success == false`, `Error.Code == ErrorCode.VolumeCorrupt`.
- `Parse_WithTooShortSpan_ReturnsVolumeCorrupt` — source shorter than 20 bytes; assert
  `ErrorCode.VolumeCorrupt`.
- `Parse_WithUnknownVersion_ReturnsVolumeIncompatibleVersion` — header with version field = 2;
  assert `ErrorCode.VolumeIncompatibleVersion`.
- `Parse_WithVersion1AndNoneFlags_Succeeds` — well-formed header with `BlobFlags.None`;
  asserts `Result.Success == true`, `flags == BlobFlags.None`.
- `Write_SetsCorrectMagicBytes` — after `Write`, reads bytes 0–3 and asserts they equal
  ASCII `"FSBL"`.
- `Write_SetsVersionOne` — reads bytes 4–5 as little-endian `uint16`, asserts == 1.

**Class: `CryptoPipelineTests`**

- `Encrypt_ThenDecrypt_ProducesOriginalPlaintext` — 256-byte plaintext, 32-byte all-zeros DEK,
  arbitrary AAD; asserts ciphertext buffer != plaintext, plaintext recovered after decrypt.
- `Encrypt_ThenDecrypt_EmptyPlaintext_Succeeds` — zero-byte plaintext; asserts encrypt/decrypt
  succeed, `bytesWritten == 0` on decrypt.
- `Encrypt_OutputLength_IsHeaderPlusCiphertextPlusTag` — after encrypt, `bytesWritten ==
  BlobHeader.HeaderSize + plaintext.Length + BlobHeader.TagSize`.
- `Encrypt_GeneratesFreshNonceEachCall` — encrypt same plaintext twice; assert the nonce bytes
  in the two output buffers differ.
- `Decrypt_WithTamperedCiphertext_ReturnsDecryptionFailed` — encrypt, flip one ciphertext byte,
  decrypt; assert `ErrorCode.DecryptionFailed`.
- `Decrypt_WithWrongKey_ReturnsDecryptionFailed` — encrypt with key A, decrypt with key B;
  assert `ErrorCode.DecryptionFailed`.
- `Decrypt_WithTamperedTag_ReturnsDecryptionFailed` — flip one byte in the tag region; assert
  `ErrorCode.DecryptionFailed`.
- `Decrypt_WithTamperedHeader_ReturnsVolumeCorrupt` — flip magic byte; assert
  `ErrorCode.VolumeCorrupt`.
- `Decrypt_WithWrongAad_ReturnsDecryptionFailed` — encrypt with AAD "A", decrypt with AAD "B";
  assert `ErrorCode.DecryptionFailed`.
- `Encrypt_WithWrongDekLength_ReturnsUnknown` — 16-byte DEK (not 32); assert
  `ErrorCode.Unknown`.
- `Encrypt_WithOutputBufferTooSmall_ReturnsUnknown` — output buffer sized one byte smaller than
  `HeaderSize + plaintext.Length + TagSize`; assert `ErrorCode.Unknown`.
- `Decrypt_WithBlobTooShort_ReturnsUnknown` — blob shorter than `HeaderSize + TagSize`; assert
  `ErrorCode.Unknown`.
- `Decrypt_WithOutputBufferTooSmall_ReturnsUnknown` — output buffer sized one byte smaller than
  the plaintext length; assert `ErrorCode.Unknown`.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (`dotnet build --warnaserror`)
- [ ] All new tests pass
- [ ] No existing tests break
- [ ] `dotnet format --verify-no-changes` reports no changes
- [ ] DEK bytes appear in no log call or `ErrorContext.Message` (code review)
- [ ] `AesGcm` is always disposed — every code path through Encrypt/Decrypt exits via `using` (code review)
- [ ] No new `ErrorCode` enum members added (ErrorCode is PR 1.1's scope; the three missing codes noted in the drift section may be reconciled in a later PR)

## Line-of-code budget

- `src/FlashSkink.Core/Crypto/BlobFlags.cs` — ~15 lines
- `src/FlashSkink.Core/Crypto/BlobHeader.cs` — ~80 lines
- `src/FlashSkink.Core/Crypto/CryptoPipeline.cs` — ~120 lines
- `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` — ~220 lines
- Total: ~215 lines non-test, ~220 lines test

## Non-goals

- Do NOT wire `CryptoPipeline` into any pipeline (WritePipeline, ReadPipeline) — those are Phase 2.
- Do NOT implement key zeroization inside CryptoPipeline — the DEK is the caller's (`VolumeLifecycle`, §1.3) responsibility.
- Do NOT add `VaultCorrupt`, `VaultVersionUnsupported`, or `CryptoFailed` to `ErrorCode` — out of scope; tracked as a drift note above.
- Do NOT add `IMemoryOwner<byte>` allocation inside CryptoPipeline — the caller owns all buffers.
- Do NOT add compression logic — BlobFlags enum is defined here but decompression/compression are Phase 2 (§2.1).
