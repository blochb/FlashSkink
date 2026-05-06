# fix: blob header authentication hardening

**Branch:** gemini-review
**Blueprint sections:** §13.6 (blob format), §18 (key hierarchy / crypto), §6 (Result pattern)

## Scope

Two complementary security fixes surfaced by cryptographic review of `BlobHeader.cs` and
`CryptoPipeline.cs`:

1. **Flag-bit validation** (`BlobHeader.Parse`) — reject blobs whose 16-bit flags field
   contains bits outside the two currently defined values (`CompressedLz4`, `CompressedZstd`).
   Provides defence-in-depth and a clear diagnostic at the parse boundary.

2. **Header-in-AAD** (`CryptoPipeline`) — bind the entire 20-byte header into the AES-GCM
   Additional Authenticated Data so that any bit-flip in magic, version, or flags causes
   `DecryptionFailed` rather than silent misrouting downstream.

Both close the same surface (malleable plaintext header), so they ship together.

## Files to create

_(none)_

## Files to modify

- `src/FlashSkink.Core/Crypto/BlobHeader.cs` — add `AllValidFlagsMask` constant; add
  unknown-bit guard in `Parse` before decoding flags (~8 lines)
- `src/FlashSkink.Core/Crypto/CryptoPipeline.cs` — modify `Encrypt` and `Decrypt` to
  build combined AAD (`header[..HeaderSize] || caller_aad`) and pass it to AES-GCM (~20 lines)
- `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` — add four new tests covering
  the two findings (~60 lines)

## Dependencies

_(none — no new NuGet packages or project references)_

## Public API surface

No public signatures change. The `aad` parameter of `Encrypt` and `Decrypt` retains its
type and meaning; header-binding is transparent to callers. `BlobHeader.Parse` return type
and `out` parameters are unchanged; the new guard adds one new `ErrorCode.VolumeCorrupt`
failure path.

## Internal types

`private const ushort AllValidFlagsMask` added to `BlobHeader` — the OR of all defined
`BlobFlags` values, used to test for unknown bits.

## Method-body contracts

### `BlobHeader.Parse` (modified)

After reading `rawFlags`, before assigning `flags`:
- If `(rawFlags & ~AllValidFlagsMask) != 0` → return
  `Result.Fail(ErrorCode.VolumeCorrupt, "Blob header contains unknown flag bits: 0x{rawFlags:X4}.")`.
- Only then assign `flags = (BlobFlags)rawFlags`.

No other changes to control flow.

### `CryptoPipeline.Encrypt` (modified)

Precondition added: `aad.Length > 512` → `Result.Fail(ErrorCode.Unknown, "AAD is too long.")`.
(Guards the `stackalloc` below from a caller inadvertently passing a huge span.)

After `BlobHeader.Write` writes the header to `output[..HeaderSize]`:

```
Span<byte> fullAad = stackalloc byte[BlobHeader.HeaderSize + aad.Length];
output[..BlobHeader.HeaderSize].CopyTo(fullAad);
aad.CopyTo(fullAad[BlobHeader.HeaderSize..]);
aes.Encrypt(nonce, plaintext, ciphertext, tag, fullAad);
```

`fullAad` is consumed synchronously before any `await` — no `stackalloc`-across-await issue
(principle 20; this method is synchronous).

### `CryptoPipeline.Decrypt` (modified)

Same `aad.Length` guard. After `BlobHeader.Parse` succeeds:

```
Span<byte> fullAad = stackalloc byte[BlobHeader.HeaderSize + aad.Length];
blob[..BlobHeader.HeaderSize].CopyTo(fullAad);
aad.CopyTo(fullAad[BlobHeader.HeaderSize..]);
aes.Decrypt(nonce, ciphertext, tag, plaintext, fullAad);
```

`aes.Decrypt` throws `CryptographicException` if the tag doesn't verify — caught by the
existing handler → `ErrorCode.DecryptionFailed`. No change to catch block.

## Integration points

- `AesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad)` — 6-argument overload, unchanged
  call site; only the `aad` argument widens.
- `AesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad)` — same.
- All callers of `CryptoPipeline.Encrypt` / `Decrypt` continue to pass their existing
  `aad` spans unchanged; header-binding is internal.

## Principles touched

- Principle 1 (Core never throws across public API — new guard returns `Result.Fail`)
- Principle 6 (zero-knowledge at every external boundary — header now authenticated)
- Principle 20 (`stackalloc` never crosses an `await` boundary — synchronous method, safe)

## Test spec

### `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs`

New test methods (append to existing class, section `// ── Header auth hardening ──`):

- `Parse_UnknownFlagBits_ReturnsVolumeCorrupt`
  Writes a valid header with flags `0x00FF` (all bits in low byte set; bits 2–7 are unknown).
  Calls `BlobHeader.Parse`. Asserts `result.Success == false` and
  `result.Error!.Code == ErrorCode.VolumeCorrupt`.

- `Parse_KnownFlagBits_Succeeds`
  Writes a valid header with flags `BlobFlags.CompressedLz4 | BlobFlags.CompressedZstd` (`0x0003`).
  Calls `BlobHeader.Parse`. Asserts `result.Success == true` and `flags == (BlobFlags)0x0003`.
  (Note: `CompressionService` rejects this combination at decompression time — `Parse` does not.)

- `Decrypt_TamperedFlagByte_ReturnsDecryptionFailed`
  Encrypts 16 bytes of plaintext. Flips `blob[6]` (the flags low byte) by XOR `0x01`.
  Calls `Decrypt` with the same DEK and AAD.
  Asserts `result.Error!.Code == ErrorCode.DecryptionFailed`.

- `Decrypt_TamperedVersionByte_ReturnsVolumeIncompatibleVersion`
  Encrypts 16 bytes. Sets `blob[4]` (version low byte) to `0x02`.
  Asserts `result.Error!.Code == ErrorCode.VolumeIncompatibleVersion`.
  (This is caught by `BlobHeader.Parse` before decryption — no change from current
  behaviour, but the test confirms the interaction with the new combined-AAD flow.)

## Acceptance criteria

- [ ] Builds with zero warnings on all targets
- [ ] All new tests pass
- [ ] No existing tests break (round-trip tests unaffected — both sides use combined AAD)
- [ ] `dotnet format --verify-no-changes` clean

## Line-of-code budget

- `src/FlashSkink.Core/Crypto/BlobHeader.cs` — ~8 lines added
- `src/FlashSkink.Core/Crypto/CryptoPipeline.cs` — ~20 lines added / modified
- `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` — ~60 lines added
- Total: ~28 lines non-test, ~60 lines test

## Non-goals

- Do NOT change the on-disk blob format version (`SupportedVersion` stays at 1).
  No migration needed — no blobs are deployed.
- Do NOT change caller `aad` construction in `WritePipeline` or any other caller.
- Do NOT add per-field AAD slices (e.g., `HeaderForAad()` helper on `BlobHeader`) —
  `CryptoPipeline` owns the concatenation internally; callers stay unchanged.
- Do NOT fix the `CompressionService.Unwrap` pre-allocation concern (a separate
  issue requiring a size-limited decompressor API, not in scope here).
