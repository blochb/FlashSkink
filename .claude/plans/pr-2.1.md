# PR 2.1 — File type and entropy detection

**Branch:** pr/2.1-file-type-entropy-detection
**Blueprint sections:** §17.1, §17.2, §17.3, §17.4, §17.5, §9.3, §9.4
**Dev plan section:** phase-2 §2.1

## Scope

Introduces three types in `FlashSkink.Core/Engine/` that together answer two questions at pipeline entry: "what kind of file is this?" and "is it worth compressing?". `MagicBytes` is a static data class holding all byte signatures and the `KnownExtensions` map. `FileTypeService` uses both signals (magic bytes and file extension) to produce a `FileTypeResult`. `EntropyDetector` uses the same static data to decide whether compression should be attempted. No pipeline integration happens in this PR — that lives in §2.5.

## Files to create

- `src/FlashSkink.Core/Engine/MagicBytes.cs` — static class with `static readonly byte[]` signatures, `FrozenDictionary<string, string>` `KnownExtensions` map, and `FrozenDictionary<string, string>` `CompressedExtensions` set; ~90 lines
- `src/FlashSkink.Core/Engine/FileTypeResult.cs` — `sealed record FileTypeResult(string? Extension, string? MimeType)`; ~10 lines
- `src/FlashSkink.Core/Engine/FileTypeService.cs` — `sealed class FileTypeService` with `Detect` method; ~90 lines
- `src/FlashSkink.Core/Engine/EntropyDetector.cs` — `sealed class EntropyDetector` with `IsCompressible` method; ~60 lines
- `tests/FlashSkink.Tests/Engine/FileTypeServiceTests.cs` — unit tests; ~160 lines
- `tests/FlashSkink.Tests/Engine/EntropyDetectorTests.cs` — unit tests; ~80 lines

## Files to modify

None. `System.Collections.Frozen` is in-box on .NET 10; no project file changes expected.

## Dependencies

- NuGet: None new. `System.Collections.Frozen` is part of .NET 8+ BCL; no package reference needed.
- Project references: None new.

## Public API surface

### `FlashSkink.Core.Engine.FileTypeResult` (sealed record)

```csharp
namespace FlashSkink.Core.Engine;

/// <summary>
/// Holds the detected extension and MIME type for a file.
/// Either field may be null when the respective signal is absent or ambiguous.
/// </summary>
public sealed record FileTypeResult(
    string? Extension,   // lower-case, dot-prefixed (".jpg") or null
    string? MimeType);   // "image/jpeg" or null
```

### `FlashSkink.Core.Engine.FileTypeService` (sealed class)

```csharp
namespace FlashSkink.Core.Engine;

/// <summary>
/// Detects file type from a filename and up to 16 header bytes.
/// Never throws. Returns null fields when a signal is absent or unrecognised.
/// Pure function — no I/O, no allocation beyond the returned record.
/// </summary>
public sealed class FileTypeService
{
    /// <summary>
    /// Detects the file type. Caller reads 16 bytes via stackalloc and passes
    /// header[..bytesRead]. Magic-byte MIME wins over extension MIME on conflict;
    /// the original extension is always preserved as-given (lower-cased, dot-prefixed).
    /// </summary>
    public FileTypeResult Detect(string fileName, ReadOnlySpan<byte> header);
}
```

### `FlashSkink.Core.Engine.EntropyDetector` (sealed class)

```csharp
namespace FlashSkink.Core.Engine;

/// <summary>
/// Decides whether a file is a candidate for compression based on its
/// known extension or magic-byte signature. Returns false for formats
/// that are already compressed. Unknown content defaults to true —
/// the pipeline attempts compression and the no-gain rule (§2.2) handles
/// actual incompressibles without penalising plain-text files with no extension.
/// </summary>
public sealed class EntropyDetector
{
    /// <summary>
    /// Returns false when extension or header indicates already-compressed content.
    /// Returns true otherwise, including when both signals are absent.
    /// </summary>
    public bool IsCompressible(string? extension, ReadOnlySpan<byte> header);
}
```

### `FlashSkink.Core.Engine.MagicBytes` (internal static class)

Not public — consumed only by `FileTypeService` and `EntropyDetector` within the assembly. Documented in "Internal types" below.

## Internal types

### `MagicBytes` (internal static class)

Owns all byte-signature constants and lookup tables. Both `FileTypeService` and `EntropyDetector` read from this single source.

```csharp
internal static class MagicBytes
{
    // Signatures — static readonly byte[]
    internal static readonly byte[] Jpeg   = [0xFF, 0xD8, 0xFF];
    internal static readonly byte[] Png    = [0x89, 0x50, 0x4E, 0x47];
    internal static readonly byte[] Gif    = [0x47, 0x49, 0x46, 0x38];
    internal static readonly byte[] Riff       = [0x52, 0x49, 0x46, 0x46]; // RIFF container — shared by WebP, WAV, AVI
    internal static readonly byte[] WebPMarker = [0x57, 0x45, 0x42, 0x50]; // "WEBP" at offset 8 within RIFF
    internal static readonly byte[] Pdf    = [0x25, 0x50, 0x44, 0x46];
    internal static readonly byte[] Zip    = [0x50, 0x4B, 0x03, 0x04];
    internal static readonly byte[] MsDoc  = [0xD0, 0xCF, 0x11, 0xE0];
    internal static readonly byte[] GZip   = [0x1F, 0x8B];
    internal static readonly byte[] BZip2  = [0x42, 0x5A, 0x68];
    internal static readonly byte[] SevenZ = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    internal static readonly byte[] Rar    = [0x52, 0x61, 0x72, 0x21];
    internal static readonly byte[] Mp4    = [0x66, 0x74, 0x79, 0x70]; // at offset 4
    internal static readonly byte[] WebM   = [0x1A, 0x45, 0xDF, 0xA3];
    internal static readonly byte[] Mp3    = [0x49, 0x44, 0x33];
    internal static readonly byte[] Ogg    = [0x4F, 0x67, 0x67, 0x53];
    internal static readonly byte[] Flac   = [0x66, 0x4C, 0x61, 0x43];

    // Extension → MIME type for extensions whose magic is ambiguous or absent.
    // FrozenDictionary is initialised once at type load; lookup uses OrdinalIgnoreCase.
    internal static readonly FrozenDictionary<string, string> KnownExtensions;

    // Extensions of already-compressed formats.
    // Used by EntropyDetector when magic bytes are absent.
    internal static readonly FrozenSet<string> CompressedExtensions;
}
```

`KnownExtensions` maps (minimum set — only extensions that have a defined MIME and that the pipeline surfaces to the user or stores in the brain):

| Extension | MIME type |
|---|---|
| `.jpg` / `.jpeg` | `image/jpeg` |
| `.png` | `image/png` |
| `.gif` | `image/gif` |
| `.webp` | `image/webp` |
| `.pdf` | `application/pdf` |
| `.zip` | `application/zip` |
| `.doc` | `application/msword` |
| `.docx` | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` |
| `.xls` | `application/vnd.ms-excel` |
| `.xlsx` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` |
| `.ppt` | `application/vnd.ms-powerpoint` |
| `.pptx` | `application/vnd.openxmlformats-officedocument.presentationml.presentation` |
| `.jar` | `application/java-archive` |
| `.epub` | `application/epub+zip` |
| `.gz` | `application/gzip` |
| `.bz2` | `application/x-bzip2` |
| `.7z` | `application/x-7z-compressed` |
| `.rar` | `application/x-rar-compressed` |
| `.mp4` | `video/mp4` |
| `.webm` | `video/webm` |
| `.mp3` | `audio/mpeg` |
| `.ogg` | `audio/ogg` |
| `.flac` | `audio/flac` |
| `.txt` | `text/plain` |
| `.html` / `.htm` | `text/html` |
| `.css` | `text/css` |
| `.js` | `text/javascript` |
| `.json` | `application/json` |
| `.xml` | `application/xml` |
| `.svg` | `image/svg+xml` |
| `.csv` | `text/csv` |

`CompressedExtensions` is the set of extensions (all in `KnownExtensions` that map to already-compressed formats): `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.pdf`, `.zip`, `.docx`, `.xlsx`, `.pptx`, `.jar`, `.epub`, `.gz`, `.bz2`, `.7z`, `.rar`, `.mp4`, `.webm`, `.mp3`, `.ogg`, `.flac`.

**Interpretive expansion beyond blueprint §17.3.** Blueprint §17.3 enumerates the magic-byte → MIME table but does not define an extension → MIME table. The plan extrapolates `KnownExtensions` to cover the magic-byte table extensions plus a small set of common text formats with no magic signature: `.txt`, `.html`/`.htm`, `.css`, `.js`, `.json`, `.xml`, `.svg`, `.csv`. Rationale: these are the formats most likely to be encountered with no magic bytes (plain-text content), and giving them a MIME entry means the brain stores a meaningful `MimeType` for them rather than `null`. **Reviewer sign-off required** — if any of these should be omitted (or others added), call it out at Gate 1.

## Method-body contracts

### `FileTypeService.Detect(string fileName, ReadOnlySpan<byte> header)`

- **Preconditions:** `fileName` may be null or any string (null-safe internally — null is treated as "no extension"). `header` may be empty (e.g. zero-byte file).
- **Returns:** `FileTypeResult` with nullable fields per the four combinations in the dev plan.
- **Algorithm:**
  1. `extension = Path.GetExtension(fileName ?? "").ToLowerInvariant(); if (extension.Length == 0) extension = null;` — `Path.GetExtension` returns `""` (not null) when there's no extension; the explicit `null` normalization here means the value is null-or-non-empty for the rest of the method.
  2. `magicMime = TryMatchMagic(header)` — tries each signature via `ReadOnlySpan<byte>.StartsWith`. Two offset-non-zero cases are encoded explicitly:
     - **MP4** — `header.Length >= 8 && header[4..].StartsWith(Mp4)` (signature `66 74 79 70` at offset 4).
     - **WebP** — `header.Length >= 12 && header.StartsWith(Riff) && header[8..].StartsWith(WebPMarker)` (RIFF prefix at offset 0, `WEBP` marker at offset 8). The bare RIFF prefix is **not** treated as a WebP match — WAV (`RIFF....WAVE`) and AVI (`RIFF....AVI `) share the prefix and would otherwise false-positive as `image/webp`. RIFF without a recognised offset-8 marker yields no magic-byte MIME (caller falls back to extension lookup).

     Returns null if no signature matches.
  3. **ZIP disambiguation via `KnownExtensions` (single source of truth).** If `magicMime == "application/zip"` and `extension is not null` and `KnownExtensions.TryGetValue(extension, out var extMime)` and `extMime != "application/zip"` → `magicMime = extMime`. This reuses the existing `KnownExtensions` entries for `.docx`, `.xlsx`, `.pptx`, `.jar`, `.epub` rather than declaring a separate `ZipDisambiguation` table — one table, no duplication. A `.zip` extension leaves `magicMime` unchanged because its `KnownExtensions` value is `application/zip`.
  4. `mimeType = magicMime ?? (extension is not null && KnownExtensions.TryGetValue(extension, out var e) ? e : null)`.
  5. Return `new FileTypeResult(extension, mimeType)`.
- **Errors:** None — never throws.

### `EntropyDetector.IsCompressible(string? extension, ReadOnlySpan<byte> header)`

- **Preconditions:** Both parameters may be null/empty.
- **Returns:** `false` if extension is in `CompressedExtensions` OR if magic-byte check identifies an already-compressed format (JPEG, PNG, GIF, WebP, ZIP, GZip, BZip2, 7Z, RAR, MP4, WebM, MP3, OGG, FLAC, PDF). `true` otherwise, including when both signals are absent/unknown.
- **Algorithm:**
  1. If `extension` is non-null and in `CompressedExtensions` → return `false`.
  2. Check magic bytes against the compressed-format signatures above → return `false` on any match. WebP uses the same offset-8 check as `FileTypeService` (`header.StartsWith(Riff) && header.Length >= 12 && header[8..].StartsWith(WebPMarker)`). A bare RIFF prefix is **not** treated as compressed — uncompressed WAV PCM and AVI streams are compressible and must not be excluded by the RIFF prefix alone.
  3. Return `true`.
- **Errors:** None — never throws.

## Integration points

No integration with other types in this PR — `FileTypeService` and `EntropyDetector` are consumed by `WritePipeline` in §2.5. This PR is self-contained.

## Principles touched

- **Principle 1** (Core never throws across its public API) — both `FileTypeService.Detect` and `EntropyDetector.IsCompressible` qualify for the **pure-function sanctioned exception** added to Principle 1 in this PR's `CLAUDE.md` diff: total functions over their inputs, no I/O, no failure path. Returns are raw values (`FileTypeResult`, `bool`); XML doc comments state "Never throws" explicitly per the carve-out.
- **Principle 8** (Core holds no UI framework reference) — new files have no Avalonia reference.
- **Principle 12** (OS-agnostic by default) — no OS APIs; detection is pure byte matching.
- **Principle 18** (allocation-conscious hot paths) — `static readonly byte[]` signatures; `FrozenDictionary` and `FrozenSet` initialised once; lookups use `ReadOnlySpan<byte>.StartsWith`; `Detect` allocates only the returned record.
- **Principle 20** (`stackalloc` never crosses `await`) — the 16-byte header `stackalloc` is in the caller (§2.5 pipeline stage); the methods here are synchronous and accept `ReadOnlySpan<byte>`.

## Test spec

**Test data ownership.** `MagicBytes` is `internal`. Tests live in the separate `FlashSkink.Tests` assembly and **author their own byte arrays inline** rather than referencing `MagicBytes.*` constants — no `[InternalsVisibleTo]` is added to `FlashSkink.Core`. Reasons: (a) keeps `MagicBytes` truly internal, (b) avoids tautological tests where the production constant *is* the expected value (a typo in `MagicBytes.Jpeg` would silently pass), (c) makes the tests legible standalone — a reviewer reading `[0xFF, 0xD8, 0xFF]` immediately sees "JPEG SOI marker" without having to follow a constant reference.

### `tests/FlashSkink.Tests/Engine/FileTypeServiceTests.cs`

```
class FileTypeServiceTests
```

- `Detect_JpegHeader_ReturnsJpegExtensionAndMime` — header `FF D8 FF ...`, fileName `photo.jpg` → `(".jpg", "image/jpeg")`.
- `Detect_PngHeader_ReturnsPngMime` — PNG magic, fileName `image.PNG` → `(".png", "image/png")` (extension lower-cased).
- `Detect_ZipHeaderWithDocxExtension_ReturnsOoxml` — ZIP magic bytes, fileName `report.docx` → `(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")`.
- `Detect_ZipHeaderWithNoKnownExtension_ReturnsApplicationZip` — ZIP magic, fileName `archive.zip` → `(".zip", "application/zip")`.
- `Detect_ZipHeaderWithXlsxExtension_ReturnsSpreadsheetMime` — ZIP magic, fileName `data.xlsx` → `(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")`.
- `Detect_ZipHeaderWithPptxExtension_ReturnsPresentationMime` — ZIP magic, fileName `slides.pptx` → `(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")`.
- `Detect_ZipHeaderWithJarExtension_ReturnsJavaArchiveMime` — ZIP magic, fileName `lib.jar` → `(".jar", "application/java-archive")`.
- `Detect_ZipHeaderWithEpubExtension_ReturnsEpubZipMime` — ZIP magic, fileName `book.epub` → `(".epub", "application/epub+zip")`.
- `Detect_Mp4ShortHeader_DoesNotMatch` — header of 6 bytes whose tail happens to be `66 74 79 70` (e.g. `00 00 66 74 79 70`) — but length is below the `>= 8` guard so the offset-4 read would run past the end. Result: MimeType is **not** `"video/mp4"` (the `header.Length >= 8` guard rejects). Validates that the bound check is enforced.
- `Detect_Mp4Header_ReturnsMp4Mime` — bytes `00 00 00 18 66 74 79 70 ...` (offset-4 signature), fileName `video.mp4` → `(".mp4", "video/mp4")`.
- `Detect_NullFileName_DoesNotThrow` — `fileName: null`, JPEG magic header → `(null, "image/jpeg")` (null fileName is treated as no extension; no NRE).
- `Detect_UnknownMagicKnownExtension_ReturnsMimeFromExtension` — random non-matching header, fileName `doc.txt` → `(".txt", "text/plain")`.
- `Detect_UnknownMagicUnknownExtension_ReturnsBothNull` — random header, fileName `noext` → `(null, null)`.
- `Detect_NoExtensionKnownMagic_ReturnsNullExtensionWithMime` — JPEG magic, fileName `photo` (no extension) → `(null, "image/jpeg")`.
- `Detect_EmptyHeader_DoesNotThrow` — empty `ReadOnlySpan<byte>`, any fileName → no exception, result may have null MIME.
- `Detect_GifHeader_ReturnsGifMime` — GIF magic → `(".gif", "image/gif")`.
- `Detect_MsDocHeader_ReturnsMsWordMime` — `D0 CF 11 E0` magic → `(extension, "application/msword")`.
- `Detect_ExtensionCaseNormalized_ReturnsLowercase` — fileName `IMAGE.JPG` → extension `".jpg"`.
- `Detect_MagicOverridesExtension_OnConflict` — PNG magic bytes but `.txt` extension → MimeType is `"image/png"`, Extension is `".txt"`.
- `Detect_WebPHeader_ReturnsWebPMime` — bytes `52 49 46 46 XX XX XX XX 57 45 42 50`, fileName `image.webp` → `(".webp", "image/webp")`.
- `Detect_WavHeader_DoesNotReturnWebPMime` — bytes `52 49 46 46 XX XX XX XX 57 41 56 45` (RIFF + WAVE at offset 8), fileName `clip.wav` → MimeType is **not** `"image/webp"` (falls through to extension lookup; with no `.wav` entry in `KnownExtensions`, MimeType is null; extension is `".wav"`).
- `Detect_AviHeader_DoesNotReturnWebPMime` — bytes `52 49 46 46 XX XX XX XX 41 56 49 20` (RIFF + `AVI ` at offset 8), fileName `clip.avi` → MimeType is **not** `"image/webp"`.
- `Detect_RiffPrefixOnlyShortHeader_DoesNotReturnWebPMime` — bytes `52 49 46 46` (only 4 bytes, header truncated before offset 8), fileName `unknown` → MimeType is null (the `header.Length >= 12` guard rejects).

### `tests/FlashSkink.Tests/Engine/EntropyDetectorTests.cs`

```
class EntropyDetectorTests
```

- `IsCompressible_JpegExtension_ReturnsFalse` — extension `.jpg`, empty header → `false`.
- `IsCompressible_PngMagic_ReturnsFalse` — PNG magic, no extension → `false`.
- `IsCompressible_Mp4Magic_ReturnsFalse` — MP4 offset-4 magic → `false`.
- `IsCompressible_ZipMagic_ReturnsFalse` — ZIP magic → `false`.
- `IsCompressible_TextExtension_ReturnsTrue` — `.txt`, empty header → `true`.
- `IsCompressible_NullExtensionUnknownMagic_ReturnsTrue` — null extension, random bytes → `true`.
- `IsCompressible_GzipMagic_ReturnsFalse` — GZip magic → `false`.
- `IsCompressible_FlacMagic_ReturnsFalse` — FLAC magic → `false`.
- `IsCompressible_EmptyHeaderAndNullExtension_ReturnsTrue` — defaults to compressible for unknown content.
- `IsCompressible_PdfExtension_ReturnsFalse` — `.pdf` extension → `false`.
- `IsCompressible_WebPMagic_ReturnsFalse` — `52 49 46 46 XX XX XX XX 57 45 42 50` (RIFF + WEBP at offset 8) → `false`.
- `IsCompressible_WavHeader_ReturnsTrue` — `52 49 46 46 XX XX XX XX 57 41 56 45` (RIFF + WAVE at offset 8), null extension → `true` (uncompressed PCM is compressible; the bare RIFF prefix must not flag it as already-compressed).
- `IsCompressible_DocxExtension_ReturnsFalse` — `.docx`, empty header → `false` (OOXML container is ZIP-compressed internally).
- `IsCompressible_XlsxExtension_ReturnsFalse` — `.xlsx`, empty header → `false`.
- `IsCompressible_PptxExtension_ReturnsFalse` — `.pptx`, empty header → `false`.
- `IsCompressible_JarExtension_ReturnsFalse` — `.jar`, empty header → `false`.
- `IsCompressible_EpubExtension_ReturnsFalse` — `.epub`, empty header → `false`.

## Acceptance criteria

- [ ] Builds with zero warnings on `ubuntu-latest` and `windows-latest`
- [ ] All new tests pass; no existing tests break
- [ ] `FileTypeService.Detect` on a JPEG header returns `(".jpg", "image/jpeg")`
- [ ] `FileTypeService.Detect` on a `.docx` ZIP-signature file returns the OOXML MIME type
- [ ] `EntropyDetector.IsCompressible` returns `false` for JPEG, PNG, MP4, ZIP; `true` for plain text
- [ ] `MagicBytes` signatures are `static readonly byte[]` declared once; lookups use `ReadOnlySpan<byte>.StartsWith`
- [ ] `KnownExtensions` is a `FrozenDictionary<string, string>` initialised at type load
- [ ] `FileTypeService.Detect` makes no I/O calls and allocates only the returned `FileTypeResult`
- [ ] CI `plan-check` passes (this file exists with all required headings and blueprint citations)

## Line-of-code budget

- `src/FlashSkink.Core/Engine/MagicBytes.cs` — ~90 lines
- `src/FlashSkink.Core/Engine/FileTypeResult.cs` — ~10 lines
- `src/FlashSkink.Core/Engine/FileTypeService.cs` — ~90 lines
- `src/FlashSkink.Core/Engine/EntropyDetector.cs` — ~60 lines
- `tests/FlashSkink.Tests/Engine/FileTypeServiceTests.cs` — ~160 lines
- `tests/FlashSkink.Tests/Engine/EntropyDetectorTests.cs` — ~80 lines
- **Total: ~250 lines non-test, ~240 lines test**

## Non-goals

- Do NOT integrate `FileTypeService` or `EntropyDetector` into `WritePipeline` — that is §2.5.
- Do NOT implement `BlobFlags`, `CompressionService`, or any compression logic — those are §2.2.
- Do NOT add DI registration for `FileTypeService` or `EntropyDetector` — §2.7 wires DI.
- Do NOT read the source stream — the 16-byte header `stackalloc` and rewind happen in the pipeline caller (§2.5), not here.
- Do NOT add new `ErrorCode` values — cross-cutting decision 4 prohibits this in Phase 2.
