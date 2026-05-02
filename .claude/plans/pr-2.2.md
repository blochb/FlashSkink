# PR 2.2 — Compression service

**Branch:** pr/2.2-compression-service
**Blueprint sections:** §14.1 (step 2), §9.2, §9.6, §13.6 (flags), §28
**Dev plan section:** phase-2 §2.2

## Scope

Introduces `CompressionService` in `FlashSkink.Core/Engine/` — the compression/decompression
stage of the write and read pipelines. Selects LZ4 for payloads below 512 KB and Zstd level 3
for larger ones. Implements the no-gain rule: if the compressed result exceeds 95 % of the
input size, `TryCompress` returns `false` and the caller writes plaintext with `BlobFlags.None`.
`Decompress` dispatches on the flags field from the parsed blob header, validates `plaintextSize`
against the 4 GiB cap before allocating, and returns `ErrorCode.BlobCorrupt` on illegal flag
combinations.

Also introduces `ClearOnDisposeOwner<T>` — an `IMemoryOwner<byte>` wrapper that zeroes the
underlying buffer on `Dispose`. This wrapper is the uniform pattern for buffers carrying
plaintext or ciphertext across both pipelines (§2.5 and §2.6 consume it directly).

`CompressionService` holds a `ZstdNet.Compressor` and a `ZstdNet.Decompressor` as private
readonly fields, constructed at creation time with `CompressionOptions(level: 3)`. It
implements `IDisposable` and disposes both codec instances. Because the volume-wide
`SemaphoreSlim(1,1)` (cross-cutting decision 1) ensures single-threaded access, the codec
instances are safe to reuse across calls without additional locking. Tests use
`using var svc = new CompressionService();`.

See `## Drift notes` for deviations from the dev plan in this area.

## Files to create

- `src/FlashSkink.Core/Engine/CompressionService.cs` — `sealed class CompressionService`;
  LZ4/Zstd compress and decompress; constants; ~130 lines
- `src/FlashSkink.Core/Buffers/ClearOnDisposeOwner.cs` — `internal sealed class
  ClearOnDisposeOwner : IMemoryOwner<byte>`; zeroes buffer on dispose; ~40 lines
- `tests/FlashSkink.Tests/Engine/CompressionServiceTests.cs` — round-trip, no-gain, error-path
  tests; ~220 lines

## Files to modify

- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `PackageReference` for
  `K4os.Compression.LZ4` and `ZstdNet` (both already pinned in `Directory.Packages.props`;
  `Microsoft.IO.RecyclableMemoryStream` is deferred to §2.5 — see `## Drift notes`)

## Dependencies

- NuGet: `K4os.Compression.LZ4` 1.3.8, `ZstdNet` 1.4.5 — already in
  `Directory.Packages.props`; only `PackageReference` entries are missing from the Core csproj.
  `Microsoft.IO.RecyclableMemoryStream` is NOT added here — see `## Drift notes`.
- Project references: None new.

## Public API surface

### `FlashSkink.Core.Engine.CompressionService` (sealed class)

Summary intent: compresses and decompresses pipeline payloads using LZ4 or Zstd; enforces
the no-gain rule on compression and the 4 GiB plaintext cap on decompression.

```csharp
namespace FlashSkink.Core.Engine;

public sealed class CompressionService : IDisposable
{
    /// <summary>Files smaller than this threshold are compressed with LZ4.</summary>
    public const int Lz4ThresholdBytes = 512 * 1024;

    /// <summary>
    /// Compressed output exceeding this fraction of input size is rejected (no-gain rule).
    /// </summary>
    public const double NoGainThreshold = 0.95;

    /// <summary>Maximum plaintext size accepted by Decompress.</summary>
    public const long MaxPlaintextBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Creates a new instance; allocates native Zstd codec handles.</summary>
    public CompressionService();

    /// <summary>Disposes the native Zstd compressor and decompressor handles.</summary>
    public void Dispose();

    /// <summary>
    /// Attempts to compress <paramref name="input"/>. Selects LZ4 when
    /// <c>input.Length &lt; Lz4ThresholdBytes</c>, Zstd level 3 otherwise.
    /// Returns false (output = null, flags = None, writtenBytes = 0) when compressed size
    /// exceeds <c>NoGainThreshold * input.Length</c> — caller writes plaintext with no flag.
    /// </summary>
    public bool TryCompress(
        ReadOnlyMemory<byte> input,
        out IMemoryOwner<byte>? output,
        out BlobFlags flags,
        out int writtenBytes);

    /// <summary>
    /// Decompresses <paramref name="compressed"/> into <paramref name="destination"/>,
    /// dispatching on <paramref name="flags"/>. <c>BlobFlags.None</c> performs a plain copy.
    /// Rejects <paramref name="plaintextSize"/> &gt; MaxPlaintextBytes with
    /// ErrorCode.FileTooLong before any allocation. Returns ErrorCode.BlobCorrupt on illegal
    /// flag combinations (both Lz4 and Zstd set) or on decompressor failure.
    /// </summary>
    public Result Decompress(
        ReadOnlyMemory<byte> compressed,
        BlobFlags flags,
        long plaintextSize,
        IMemoryOwner<byte> destination,
        out int writtenBytes);
}
```

## Internal types

### `FlashSkink.Core.Buffers.ClearOnDisposeOwner` (internal sealed class)

Wraps an `IMemoryOwner<byte>` rented from `MemoryPool<byte>.Shared` and calls
`CryptographicOperations.ZeroMemory` over the full rented span before returning the buffer.
The `Memory` property exposes a slice of exactly `length` bytes (not the full rented
over-allocation) — same pattern as the `SlicedOwner` in blueprint §9.2.

```csharp
internal sealed class ClearOnDisposeOwner : IMemoryOwner<byte>
{
    public Memory<byte> Memory { get; }   // sliced to exactly 'length' bytes
    public void Dispose();                // zeros full rented span, then returns to pool
}

// Factory method — pool defaults to MemoryPool<byte>.Shared; tests supply a recording pool.
internal static ClearOnDisposeOwner Rent(int length, MemoryPool<byte>? pool = null);
```

`Dispose` is idempotent (double-dispose is safe via `Interlocked.Exchange` on the inner owner).
The `pool` parameter exists solely to make zero-verification testable without relying on
`ArrayPool` return ordering or reflection into BCL internals.

## Method-body contracts

### `CompressionService.TryCompress`

- **Preconditions:** `input` may be empty (empty input is compressible; result is a small LZ4
  header — no-gain rule will reject it anyway for typical zero-byte inputs). No `ct` parameter
  — compression is CPU-bound and synchronous; callers wrap in `Task.Run` on hot paths if
  needed (§2.5 does not; V1 read-once-into-buffer model is synchronous after the stream read).
- **Algorithm:**
  1. If `input.Length < Lz4ThresholdBytes`: rent from `MemoryPool<byte>.Shared` at
     `LZ4Codec.MaximumOutputSize(input.Length)` bytes; call
     `LZ4Codec.Encode(input.Span, rentedSpan)` → `written` bytes; `flags = CompressedLz4`.
  2. Otherwise: compute output bound as `input.Length + (input.Length >> 3) + 128`
     (conservative zstd frame overhead; the no-gain check at step 3 discards the result if
     it's too large anyway). Rent that many bytes from `MemoryPool<byte>.Shared`. Obtain the
     input as `byte[]` via `MemoryMarshal.TryGetArray(input, out var seg)` if the underlying
     memory is array-backed (it always will be when called from §2.5's pipeline buffer);
     otherwise copy via `input.ToArray()`. Call `_compressor.Wrap(inputArray, rentedArray, 0)`
     → `written` bytes. `flags = CompressedZstd`.
  3. If `written > NoGainThreshold * input.Length`: dispose the rented/stream buffer;
     set `output = null`, `flags = BlobFlags.None`, `writtenBytes = 0`; return `false`.
  4. Wrap the rented buffer in `ClearOnDisposeOwner` sliced to `written` bytes;
     set `output`, `writtenBytes`; return `true`.
- **Does not throw.** Wraps any `LZ4Exception` or `ZstdException` as `false` return (no-gain
  semantics — a compression failure is treated as incompressible content, not an error, at
  this layer). The pipeline caller (§2.5) has no error-path to handle here.
- **Errors:** None propagated — returns `false` on any compression failure.

### `CompressionService.Decompress`

- **Preconditions:** `destination.Memory.Length >= plaintextSize` (caller allocates after cap
  check). `flags` is one of `None`, `CompressedLz4`, `CompressedZstd`. Negative `plaintextSize`
  values are rejected defensively inside the method (see step 1) rather than left as undefined
  behaviour — keeps the public boundary clean per Principle 1.
- **Algorithm:**
  1. If `plaintextSize < 0` → `Result.Fail(ErrorCode.BlobCorrupt, "plaintextSize is negative", ...)`.
  2. If `plaintextSize > MaxPlaintextBytes` → `Result.Fail(ErrorCode.FileTooLong, ...)`.
  3. If both `CompressedLz4` and `CompressedZstd` are set simultaneously →
     `Result.Fail(ErrorCode.BlobCorrupt, "Illegal BlobFlags combination", ...)`.
  4. Dispatch:
     - `BlobFlags.None`: `compressed.Span.CopyTo(destination.Memory.Span)`;
       `writtenBytes = compressed.Length`.
     - `CompressedLz4`: `LZ4Codec.Decode(compressed.Span, destination.Memory.Span)` →
       `writtenBytes`. If decoded count != `plaintextSize` →
       `Result.Fail(ErrorCode.BlobCorrupt, ...)`.
     - `CompressedZstd`: obtain source as `byte[]` via `MemoryMarshal.TryGetArray` or
       `.ToArray()`. Obtain destination `byte[]` the same way from `destination.Memory`.
       Call `_decompressor.Unwrap(compressedArray, destArray, 0)` → `writtenBytes`.
       If `writtenBytes != plaintextSize` → `Result.Fail(ErrorCode.BlobCorrupt, ...)`.
  5. Return `Result.Ok()` with `writtenBytes` set.
- **Catch ordering** (four separate `catch` blocks in this order):
  1. `catch (OperationCanceledException)` — belt-and-braces; Principle 14 does not strictly
     require it here (no `ct`, no `await`) but adds no cost and keeps the pattern uniform.
     `ErrorCode.Cancelled`.
  2. `catch (LZ4Exception)` → `Result.Fail(ErrorCode.BlobCorrupt, ..., ex)`.
  3. `catch (ZstdException)` → `Result.Fail(ErrorCode.BlobCorrupt, ..., ex)`.
  4. `catch (Exception)` → `Result.Fail(ErrorCode.Unknown, ..., ex)`.
- On any failure: `writtenBytes = 0`.

## Integration points

- `BlobFlags` from `FlashSkink.Core.Crypto` (§1.4 — `CompressedLz4 = 1`, `CompressedZstd = 2`,
  `None = 0`). `CompressionService` adds a `using FlashSkink.Core.Crypto;` — no project ref
  change required (same assembly).
- `Result`, `ErrorCode` from `FlashSkink.Core.Abstractions.Results` (§1.1).
- `ClearOnDisposeOwner` from `FlashSkink.Core.Buffers` — consumed internally by
  `TryCompress`; also consumed by `WritePipeline` (§2.5) and `ReadPipeline` (§2.6) which rent
  their own buffers using `ClearOnDisposeOwner.Rent(length)`.
- `MemoryPool<byte>.Shared` — BCL; `LZ4Codec` from `K4os.Compression.LZ4`;
  `Compressor`, `Decompressor`, `CompressionOptions` from `ZstdNet`.

## Principles touched

- **Principle 1** (Core never throws across its public API) — `TryCompress` returns `bool`;
  `Decompress` returns `Result`. Neither throws. Catch ordering per Principle 15.
- **Principle 13** (`CancellationToken ct` always last) — compression is synchronous and
  CPU-bound; no `ct` parameter. Not applicable to synchronous non-I/O methods.
- **Principle 14** (`OperationCanceledException` first) — `Decompress` accepts no `ct` and
  performs no `await`, so the principle does not strictly apply; the OCE catch is included
  belt-and-braces for pattern uniformity and is noted as such in the method-body contracts.
- **Principle 15** (no bare `catch (Exception)` only) — specific exception types first
  (`LZ4Exception`, `ZstdException`), then `Exception` last.
- **Principle 16** (dispose on every failure path) — `TryCompress` disposes the rented buffer
  in the no-gain path and in the catch path. `Decompress` writes into caller-supplied buffer;
  no buffer lifecycle managed here.
- **Principle 18** (allocation-conscious hot paths) — LZ4 path uses `MemoryPool<byte>.Shared`
  and `LZ4Codec.Encode(Span<byte>, Span<byte>)` with zero intermediate allocations. Zstd path
  uses `Compressor.Wrap` / `Decompressor.Unwrap` into pre-rented `MemoryPool<byte>.Shared`
  buffers; no `new MemoryStream()` or intermediate copy beyond the `TryGetArray` fast path.
  `ClearOnDisposeOwner` wraps the rented buffer and zeroes it on dispose (Principle 31 alignment).
- **Principle 19** (`IMemoryOwner<byte>` ownership) — `TryCompress` returns
  `IMemoryOwner<byte>`; caller disposes. `Decompress` writes into caller-supplied owner.
- **Principle 21** (`RecyclableMemoryStream` replaces `new MemoryStream()`) — not applicable
  in this PR; the Zstd paths use the `Compressor`/`Decompressor` buffer API and never
  instantiate a `MemoryStream`. `RecyclableMemoryStreamManager` is introduced in §2.5.

## Test spec

Tests author their own byte arrays and constants inline — no `[InternalsVisibleTo]` to reach
`ClearOnDisposeOwner`. Constants `Lz4ThresholdBytes`, `NoGainThreshold`, and `MaxPlaintextBytes`
are `public` on `CompressionService` and may be referenced directly. Each test class (or test
fixture) instantiates `CompressionService` with `using var svc = new CompressionService()` and
disposes it after the test.

### `tests/FlashSkink.Tests/Engine/CompressionServiceTests.cs`

```
class CompressionServiceTests
```

**Round-trip tests:**

- `TryCompress_SmallCompressiblePayload_ReturnsLz4AndRoundTrips` — 100 KB of repeated
  `0xAB` bytes; `TryCompress` returns `true`, `flags == CompressedLz4`; `Decompress` with the
  returned buffer, `flags`, and original length produces byte-identical output. Verifies
  ciphertext != plaintext (compressed form differs) and round-trip == input.
- `TryCompress_LargeCompressiblePayload_ReturnsZstdAndRoundTrips` — 1 MB of repeated
  `0xCD` bytes; `TryCompress` returns `true`, `flags == CompressedZstd`; `Decompress`
  round-trips byte-identically.
- `TryCompress_HighEntropyPayload_ReturnsFalse` — 200 KB of `new byte[200 * 1024]` filled
  with `Random(42).NextBytes` (pseudo-random, effectively incompressible); `TryCompress`
  returns `false`, `output == null`, `flags == BlobFlags.None`.
- `Decompress_BlobFlagsNone_CopiesInputToDestination` — arbitrary 1 KB payload, flags `None`;
  `Decompress` copies bytes verbatim; `writtenBytes == compressed.Length`.

**Threshold boundary tests:**

- `TryCompress_OneByteBelowLz4Threshold_UsesLz4` — `Lz4ThresholdBytes - 1` bytes of
  compressible content → `CompressedLz4`.
- `TryCompress_AtLz4Threshold_UsesZstd` — `Lz4ThresholdBytes` bytes of compressible content
  → `CompressedZstd` (threshold uses `<` semantics, so exactly at the boundary is Zstd).

**Error-path tests:**

- `Decompress_BothFlagsSet_ReturnsBlobCorrupt` — flags with both `CompressedLz4` and
  `CompressedZstd` set simultaneously → `Result.Success == false`,
  `ErrorCode == BlobCorrupt`.
- `Decompress_PlaintextSizeExceedsMax_ReturnsFileTooLong` — `plaintextSize` set to
  `CompressionService.MaxPlaintextBytes + 1L`; `Decompress` returns `Result.Success == false`
  with `ErrorCode.FileTooLong` before any allocation. Also tests the boundary value
  `MaxPlaintextBytes` itself, which must be accepted (`Result.Success == true` given a
  correctly-sized destination buffer and a valid `BlobFlags.None` payload).
- `Decompress_NegativePlaintextSize_ReturnsBlobCorrupt` — `plaintextSize = -1` →
  `ErrorCode.BlobCorrupt`.

**Dispose / zero tests:**

- `ClearOnDisposeOwner_DisposeZeroesBuffer` — create a `RecordingMemoryPool` (a
  `MemoryPool<byte>` subclass defined in the test file that tracks the last array it rented
  and exposes it as `byte[] LastRented`). Call `ClearOnDisposeOwner.Rent(64, recordingPool)`,
  fill the rented `Memory` with `0xFF`, dispose, then read `recordingPool.LastRented` directly
  and assert all 64 bytes are `0x00`. No reflection, no ordering assumptions.
- `ClearOnDisposeOwner_DoubleDispose_DoesNotThrow` — dispose twice; no exception.

## Acceptance criteria

- [ ] Builds with zero warnings on `ubuntu-latest` and `windows-latest`
- [ ] All new tests pass; no existing tests break
- [ ] `CompressionService.TryCompress` of a 100 KB compressible payload returns
  `BlobFlags.CompressedLz4`; round-trip via `Decompress` produces byte-identical output
- [ ] `CompressionService.TryCompress` of a 1 MB compressible payload returns
  `BlobFlags.CompressedZstd`; round-trip succeeds
- [ ] `CompressionService.TryCompress` of a high-entropy payload returns `false`
  (no-gain rejection)
- [ ] `Decompress` with both `CompressedLz4` and `CompressedZstd` set returns
  `ErrorCode.BlobCorrupt`
- [ ] `Decompress` with `BlobFlags.None` copies input to destination unchanged
- [ ] `ClearOnDisposeOwner.Dispose` zeroes the rented buffer
- [ ] Core csproj has `PackageReference` entries for `K4os.Compression.LZ4` and `ZstdNet`
- [ ] No new `ErrorCode` values added (`ErrorCode.cs` is not modified) — cross-cutting decision 4
- [ ] CI `plan-check` passes (this file exists with all required headings and `§` citations)

## Line-of-code budget

- `src/FlashSkink.Core/Engine/CompressionService.cs` — ~130 lines
- `src/FlashSkink.Core/Buffers/ClearOnDisposeOwner.cs` — ~40 lines
- `tests/FlashSkink.Tests/Engine/CompressionServiceTests.cs` — ~220 lines
- **Total: ~170 lines non-test, ~220 lines test**

## Drift notes

**1. `BlobFlags` was established by §1.4, not §2.2.**
Dev plan §2.2 states "`BlobFlags` … lives here because §2.2 is the first consumer" and names
the members `Lz4 = 1` and `Zstd = 2`. In practice, PR 1.4 (`.claude/plans/pr-1.4.md`) created
`BlobFlags` in `FlashSkink.Core.Crypto` with members `CompressedLz4 = 1 << 0` and
`CompressedZstd = 1 << 1`. The bit positions are identical; only the namespace and member names
differ. §2.2 is still the first *compression consumer*, but the type itself originates in §1.4
as an `[Flags] enum BlobFlags : ushort` in `FlashSkink.Core.Crypto.BlobFlags`. `CompressionService`
imports it with `using FlashSkink.Core.Crypto;` — no redeclaration.

**2. `CompressionService` uses `Compressor`/`Decompressor` fields, not per-call stream objects.**
Dev plan §2.2 says `CompressionService` "owns LZ4 and Zstd codecs as scoped instances
(Principle 18, §9.8)." The original plan draft used `ZstdNet.CompressionStream`/
`DecompressionStream` per call, which creates and destroys native codec handles on every
compress/decompress — violating §9.8 and adding unnecessary allocations. This PR corrects the
draft: `CompressionService` holds `private readonly Compressor _compressor` and
`private readonly Decompressor _decompressor` as fields, implements `IDisposable`, and uses
`Compressor.Wrap` / `Decompressor.Unwrap` (buffer API) throughout. This also eliminates any
need for `RecyclableMemoryStream` in `CompressionService`.

**3. `RecyclableMemoryStreamManager` — package and registration deferred past §2.2.**
Dev plan §2.2 states the singleton is "registered as a DI singleton **in this PR**." However,
`CompressionService` no longer uses `RecyclableMemoryStream` (see drift 2), so there is no
consumer here. The `PackageReference` for `Microsoft.IO.RecyclableMemoryStream` moves to §2.5
(first actual consumer: `WritePipeline`). The DI singleton registration defers to §2.7, where
`FlashSkinkVolume` wires all Core singleton registrations — the same PR where no DI composition
root yet exists.

## Non-goals

- Do NOT redeclare or modify `BlobFlags` — it was established by §1.4 in
  `FlashSkink.Core.Crypto`. `CompressionService` references it via `using`.
- Do NOT register `RecyclableMemoryStreamManager` in a DI container — that is §2.7.
- Do NOT integrate `CompressionService` into `WritePipeline` or `ReadPipeline` — those are
  §2.5 and §2.6.
- Do NOT add new `ErrorCode` values — cross-cutting decision 4 prohibits this in Phase 2.
- Do NOT implement the streaming chunk-through compress/encrypt variant — post-V1.
- Do NOT implement `VolumeContext` — that is §2.7. `MaxPlaintextBytes` lives temporarily on
  `CompressionService`; **§2.7 will move it to `VolumeContext` and update this PR's callers
  and tests to reference the new location.**
