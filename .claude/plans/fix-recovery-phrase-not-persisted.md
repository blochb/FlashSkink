# Fix — recovery phrase must not be persisted on the skink; `MnemonicService` reworked for zeroizable buffers

**Branch:** `fix/recovery-phrase-not-persisted`
**Blueprint sections touched:** §11, §18.3, §18.6 (Session Key & USB Removal Policy — keys-zeroed list), §18.8, §25 CLI listing, §29 Decisions table (A16)
**Dev plan sections touched:** phase-2 "Key constraints" deferred-methods list

## Scope

This fix lands two coupled changes in a single PR:

1. **Drop persistence.** Remove the `Settings["RecoveryPhrase"]` row from `SeedInitialSettingsAsync`. Change `FlashSkinkVolume.CreateAsync` to surface the recovery phrase to the caller exactly once via a new `VolumeCreationReceipt`, then let the caller dispose it. Brain-mirror tail exposure of the phrase is closed because nothing persists.
2. **Make the phrase zeroizable.** Replace `MnemonicService`'s `string[]`-based surface with a new `RecoveryPhrase` disposable that owns `char[][]` buffers and zeroes them on `Dispose`. The library no longer holds an immutable, unzeroizable copy of the phrase for the process lifetime.

These two changes are coupled because the receipt's `RecoveryPhrase` field is the new type, and the new type's only meaningful production constructor is `MnemonicService.Generate`. Splitting would leave a temporary type-shape mismatch between the persistence-fix PR and the rework PR.

### Why this is scoped as a single PR (Principle 23 honesty check)

V1 hasn't shipped. `MnemonicService` and the `FlashSkinkVolume` factory aren't yet on the frozen-additive-only surface (`IStorageProvider`, `IProviderSetup`, etc.). The dev plan at [phase-2-…md:299](dev-plan/phase-2-write-pipeline-and-phase-1-commit.md:299) explicitly defers `RevealRecoveryPhraseAsync` to Phase 5 *and* notes that re-display "depends on a mnemonic-at-rest encryption mechanism the blueprint does not yet pin down" — i.e. the surface has always been provisional. Reshaping it now, before the CLI consumes it, is cheap.

### Residual risks not addressed in this PR (intentional non-goals)

The `RecoveryPhrase` rework eliminates long-lived in-process string copies of the phrase. It does **not** close:
- BCL string copies created transiently during display (`Console.WriteLine`) or input (`Console.ReadLine`).
- Terminal scrollback retaining displayed output.
- Screenshot / screen-recorder / camera capture during the display window.
- Debugger or RAM disclosure during the live window between `Generate` and `Dispose`.

Three follow-up mitigations belong in the future CLI `setup`-command PR, not this one:
1. Refuse to display when `Console.IsOutputRedirected` or `Console.IsInputRedirected` (check *before* `CreateAsync` so a refused display doesn't burn a phrase the user can't see).
2. Switch to terminal alternate-screen buffer (`\e[?1049h` / `\e[?1049l`) for the display window so scrollback never sees the phrase.
3. Require explicit "I've recorded the phrase" confirmation before clearing the alt-screen and disposing the receipt.

These are listed in this plan only so the eventual CLI PR has them on file; they are NOT implemented here.

## Files to create

- `src/FlashSkink.Core.Abstractions/Crypto/RecoveryPhrase.cs` — sealed `IDisposable` class owning `char[][]` word buffers. Constructed only via `MnemonicService.Generate` (the internal ctor is `internal`, called from Core) or `RecoveryPhrase.FromUserInput` (public static factory for user-typed input). ~90 lines.
- `src/FlashSkink.Core/Orchestration/VolumeCreationReceipt.cs` — sealed record carrying `(FlashSkinkVolume Volume, RecoveryPhrase RecoveryPhrase)`. ~30 lines. **Lives in `FlashSkink.Core`, not `Abstractions`**, because its `Volume` field references `FlashSkinkVolume` (which lives in Core) — `Core.Abstractions` has no project references (Principle 8-11). `Result<VolumeCreationReceipt>` still composes cleanly because `Result<T>` is generic.

## Files to modify

- `src/FlashSkink.Core/Crypto/MnemonicService.cs` — reshape signatures: `Generate()` now returns `Result<RecoveryPhrase>`; `Validate(string[])` becomes `Validate(RecoveryPhrase)`; `ToSeed(string[])` becomes `ToSeed(RecoveryPhrase)`. Internal: replace the per-call `Dictionary<string, int>` allocation with a `FrozenDictionary<string, int>` plus `AlternateLookup<ReadOnlySpan<char>>` (.NET 9+) so lookups against the phrase's char spans don't allocate strings. `ToSeed` writes the joined mnemonic into pooled `char[]` + `byte[]` buffers (ArrayPool), runs PBKDF2 on the bytes, and zeroes both pooled buffers in `finally` before returning to the pool. ~80 line delta (net + a few).
- `src/FlashSkink.Core/Crypto/KeyVault.cs` — change `UnlockFromMnemonicAsync` parameter from `string[] words` to `RecoveryPhrase phrase`. Caller owns disposal of the phrase. The `seed`/`kek` zeroization in the `finally` block is unchanged. ~5 line delta.
- `src/FlashSkink.Core/Crypto/KeyDerivationService.cs` — single XML doc tweak: the `seed` parameter's `<see cref="MnemonicService.ToSeed"/>` reference is unchanged textually, but the *behaviour* it refers to now takes a `RecoveryPhrase`. No code change. Mentioned for completeness.
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` —
  - Drop the `RecoveryPhrase` upsert at [line 993](src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:993) and the misleading `// … Principle 26` comment at [line 992](src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:992).
  - Change `SeedInitialSettingsAsync` signature to drop the `string[] mnemonicWords` parameter.
  - Change `CreateAsync` return type from `Task<Result<FlashSkinkVolume>>` to `Task<Result<VolumeCreationReceipt>>`. On the success path, construct the receipt with the live volume and the `RecoveryPhrase` returned from `mnemonicService.Generate()` (transferring ownership to the caller). On every failure path between `Generate()` and the success return, dispose the `RecoveryPhrase` before returning the `Result.Fail(...)` so its char[] buffers are zeroed.
  - Update `CreateAsync`'s XML `<summary>` to state explicitly that the phrase is returned exactly once and is not persisted by FlashSkink anywhere.
  - ~25 line delta.
- `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs` — rewrite all 14 tests against the new shape. Tests construct `RecoveryPhrase` either via `_sut.Generate()` or `RecoveryPhrase.FromUserInput(string[])` from inline test vectors; assertions compare `ReadOnlySpan<char>` to expected strings via `MemoryExtensions.SequenceEqual` or `phrase[i].ToString()` (test-only allocation acceptable). `using` blocks around every `RecoveryPhrase` instance. ~50 line delta.
- `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs` — single call site at [line 300](tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs:300): `_mnemonic.ToSeed(words)` → wrap `words` in `RecoveryPhrase.FromUserInput(words).Value!` first, then `_mnemonic.ToSeed(phrase)`. ~5 line delta.
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs` — ~42 `CreateAsync` call sites mechanically rewritten from `(await FlashSkinkVolume.CreateAsync(...)).Value!` → `(await FlashSkinkVolume.CreateAsync(...)).Value!.Volume` (with `using` on the receipt where the test holds the value). Rewrite `CreateAsync_NewSkinkRoot_GeneratesRecoveryPhrase` to assert against the receipt's `RecoveryPhrase` (Count == 24, each word in `MnemonicService.Wordlist`). Add new test `CreateAsync_DoesNotPersistRecoveryPhrase` asserting `SELECT COUNT(*) FROM Settings WHERE Key = 'RecoveryPhrase'` returns 0 after creation. ~60 line delta.
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs` — single call site at [line 75](tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs:75) mechanical rewrite. 1 line delta.
- `BLUEPRINT.md` —
  - §11 (line 1242-1243): delete the `RevealRecoveryPhraseAsync` method declaration.
  - §11 behaviour notes (line 1267): delete the `RevealRecoveryPhraseAsync` bullet.
  - §18.3 (line 2070): replace the "shown exactly once… can be re-displayed via `RevealRecoveryPhraseAsync`…" sentence with new text: "The mnemonic is shown exactly once at setup via the `RecoveryPhrase` field of `VolumeCreationReceipt` returned by `FlashSkinkVolume.CreateAsync`. FlashSkink does not persist it. The caller is responsible for displaying it to the user before disposing the receipt; once disposed the phrase's owning `char[][]` buffers are zeroed and unrecoverable."
  - §18.6 keys-zeroed-on-close list ([line 2111](BLUEPRINT.md:2111)): add "Recovery phrase `char[][]` buffers (zeroed when `RecoveryPhrase` is disposed; ownership transfers from `MnemonicService.Generate` to the caller via `VolumeCreationReceipt`)". This puts the new disposable type on the same footing as DEK / KEK / brain key.
  - §18.8 (line 2125): rewrite the mnemonic bullet from "Mnemonic (shown to user once; not persisted by FlashSkink)" to "Mnemonic (returned exactly once via `VolumeCreationReceipt.RecoveryPhrase`; held only in `char[][]` buffers zeroed on disposal; not persisted by FlashSkink on the skink or any tail)".
  - §25 CLI surface (line 2496): delete the `flashskink-cli reveal-phrase` line.
  - §29 Decisions table (line 2842): repurpose A16 row to `(a) No re-display; caller must record at setup` with rationale "Persisting the phrase contradicts §18.8 and adds tail-surface exposure for marginal UX". Row number stays A16 to keep stable cross-references.
- `dev-plan/phase-2-write-pipeline-and-phase-1-commit.md` —
  - [line 299](dev-plan/phase-2-write-pipeline-and-phase-1-commit.md:299): remove `RevealRecoveryPhraseAsync` from the deferred-methods list.
  - [line 311](dev-plan/phase-2-write-pipeline-and-phase-1-commit.md:311): strip `RevealRecoveryPhraseAsync` from the "What Phase 2 does NOT do" entry; the parenthetical rationale ("depends on a mnemonic-at-rest encryption mechanism the blueprint does not yet pin down") becomes obsolete and the sentence rewraps around the remaining `ResetPasswordAsync` mention.

## Dependencies

- No new NuGet packages.
- No new project references.
- Uses `FrozenDictionary<TKey, TValue>.AlternateLookup<TAlternateKey>` (.NET 9+). Project targets `net10.0`, so available.

## Public API surface

### `FlashSkink.Core.Abstractions.Crypto.RecoveryPhrase` (sealed class, IDisposable)

Summary intent: zeroizable owner of a BIP-39 recovery phrase. Held only in `char[][]` buffers; `Dispose` clears every word's chars to zero. Construct via `MnemonicService.Generate` (random-generation path) or `RecoveryPhrase.FromUserInput` (user-typed recovery / reset path). The string-input factory cannot zero its source strings — the caller is responsible for understanding that the originating strings remain on the heap until GC reclaims them.

```csharp
public sealed class RecoveryPhrase : IDisposable
{
    private char[][]? _words;

    internal RecoveryPhrase(char[][] words);

    public int Count { get; }                          // throws ObjectDisposedException after Dispose
    public ReadOnlySpan<char> this[int index] { get; } // throws ObjectDisposedException after Dispose

    public static Result<RecoveryPhrase> FromUserInput(IReadOnlyList<string> words);

    public void Dispose();                              // idempotent; zeros every char[] and clears _words
}
```

- **`Generate`/factory path:** `MnemonicService.Generate` constructs each `char[]` directly from the BIP-39 wordlist's entries via `wordlist[idx].CopyTo(charBuf)`. The wordlist strings themselves are static, non-secret BIP-39 dictionary data — no zeroization needed for them.
- **`FromUserInput` path:** copies each source string's chars into a freshly-allocated `char[]`. If any source word is null, zeros any already-allocated destination buffers before returning `Result.Fail(ErrorCode.InvalidMnemonic, ...)` — partial-construction cleanup is part of the contract.
- **Indexer / Count behaviour after Dispose:** throws `ObjectDisposedException`. This is a *deliberate* deviation from Principle 1's "Core never throws across its public API boundary" rule. Justification: `RecoveryPhrase` is a value-container with `IDisposable` semantics, analogous to `Stream`, `SqliteConnection`, `SemaphoreSlim` — all of which throw `ObjectDisposedException` post-dispose. Use-after-dispose is a programming error class, not a runtime failure mode the caller should handle with a Result branch. The principle's strict reading applies to *factories* (`Generate`, `FromUserInput`) which return `Result<RecoveryPhrase>`; the type's own member access follows .NET conventions. Documented inline in `RecoveryPhrase.cs` with the principle citation.
- **`Dispose` is idempotent:** double-dispose is a no-op (the second call sees `_words == null` and returns immediately). `Dispose` never throws.
- **No finalizer:** `char[]` is a managed array; no unmanaged resources. The contract is "caller must dispose to zero the buffers"; if the caller leaks the phrase, the chars eventually get GC'd as memory pressure compacts the heap, but they are not proactively zeroed. We do not add a finalizer because finalizer-based zeroing runs on a thread we don't control, can race with the GC, and gives a false sense of security — the *contract* requires explicit disposal at the call site, and the receipt's `using` block enforces it ergonomically.

### `FlashSkink.Core.Abstractions.Models.VolumeCreationReceipt` (sealed record)

Summary intent: receipt returned exactly once from `FlashSkinkVolume.CreateAsync`. Carries the live volume and the BIP-39 recovery phrase. The phrase is not persisted anywhere by FlashSkink; losing this receipt without recording the phrase forfeits the only out-of-band recovery path.

```csharp
public sealed record VolumeCreationReceipt(
    FlashSkinkVolume Volume,
    RecoveryPhrase RecoveryPhrase);
```

- The `RecoveryPhrase` is owned by the receipt; the caller is expected to `using` the receipt (records support `IDisposable` if they declare it — but this is a record carrying a disposable, not a disposable itself; the disposal pattern is `using var receipt = ...; try { ... } finally { receipt.RecoveryPhrase.Dispose(); }`).
- **Design choice — record holding a disposable rather than disposable record:** the record stays a plain immutable carrier. The CLI is the layer that owns the disposal pattern. Wrapping disposal into the record itself would mean disposing the receipt also disposes the `Volume`, which is the wrong lifetime — the volume outlives the phrase by design (volume lives until the user closes the skink; phrase lives only across the display call). Keeping these lifetimes separate at the type level prevents that confusion.

### `FlashSkinkVolume.CreateAsync` (signature change)

```csharp
public static Task<Result<VolumeCreationReceipt>> CreateAsync(
    string skinkRoot, string password, VolumeCreationOptions options, CancellationToken ct = default);
```

XML `<summary>` rewritten — see "Files to modify" entry.

`OpenAsync` is unchanged.

### `MnemonicService` (reshape)

```csharp
// before
public Result<string[]> Generate();
public Result Validate(string[] words);
public Result<byte[]> ToSeed(string[] words);

// after
public Result<RecoveryPhrase> Generate();
public Result Validate(RecoveryPhrase phrase);
public Result<byte[]> ToSeed(RecoveryPhrase phrase);
```

`MnemonicService.Wordlist` is unchanged: `static IReadOnlyList<string> Wordlist`. The wordlist is public BIP-39 dictionary data, not secret.

### `KeyVault.UnlockFromMnemonicAsync` (signature change)

```csharp
// before
public Task<Result<byte[]>> UnlockFromMnemonicAsync(string vaultPath, string[] words, CancellationToken ct);

// after
public Task<Result<byte[]>> UnlockFromMnemonicAsync(string vaultPath, RecoveryPhrase phrase, CancellationToken ct);
```

Ownership rule: `KeyVault.UnlockFromMnemonicAsync` does NOT dispose the passed-in `RecoveryPhrase`. The caller owns disposal — typically the CLI handler that constructed it via `RecoveryPhrase.FromUserInput` and will dispose it after vault unlock succeeds or fails. Documented in the XML `<param>` comment.

## Internal types

None — both new types are public (in `Core.Abstractions`).

## Method-body contracts

### `RecoveryPhrase.FromUserInput`

- Null input → `Result.Fail(ErrorCode.InvalidMnemonic, "Recovery phrase word list cannot be null.")`.
- Any null word → zero any already-allocated destination `char[]` (`Array.Clear`) before returning `Result.Fail(ErrorCode.InvalidMnemonic, "Recovery phrase contains a null word.")`.
- Empty list is allowed at this layer — wordcount validation happens in `MnemonicService.Validate`. (Keeps `FromUserInput`'s scope narrow: it builds a buffer-owning wrapper; semantic validation is a separate concern.)
- Otherwise: allocate `new char[words.Count][]`; for each `i`, allocate a `char[words[i].Length]` and copy chars via `words[i].CopyTo(0, dest, 0, words[i].Length)`. Wrap in `RecoveryPhrase` and return `Result.Ok`.
- No `try/catch` — there are no failure modes after the null checks. (Allocation failure → `OutOfMemoryException` propagates; that's a process-wide condition, not a Result branch we handle.)

### `RecoveryPhrase.Dispose`

```csharp
public void Dispose()
{
    if (_words is null) { return; }
    foreach (var w in _words)
    {
        if (w is not null) { Array.Clear(w); }
    }
    _words = null;
}
```

Idempotent. Never throws.

### `MnemonicService.Generate`

Body changes:
- After computing the 24 indices from entropy + checksum (existing logic), allocate `var words = new char[24][];` then for each `i` set `words[i] = _wordlistArray[index].ToCharArray()`. This produces a fresh, owned `char[]` per word — distinct from the wordlist string's interned char array.
- Wrap in `new RecoveryPhrase(words)` and return `Result<RecoveryPhrase>.Ok(...)`.
- On the `Exception` catch path: any partially-allocated `char[]` entries are still in `words` if they exist — but since they originate from non-secret `_wordlistArray` strings, they don't contain anything that needs zeroing if construction fails partway. The entropy/hash buffers from the existing code path also stay non-secret post-checksum-extraction. No additional cleanup needed beyond what the existing catch already does.

### `MnemonicService.Validate`

Body changes:
- Replace `words.Length != 24` check with `phrase.Count != 24`.
- Replace the `_wordIndex.TryGetValue(word, out var idx)` lookup with `_wordIndexSpan.TryGetValue(phrase[i], out var idx)` where `_wordIndexSpan` is the new `FrozenDictionary.AlternateLookup<ReadOnlySpan<char>>`.
- Remove the explicit null check (the indexer guarantees a non-null span; if `phrase` itself is null the method throws `NullReferenceException` on `.Count` — this is a programming error, the caller can't legitimately pass null).
- Otherwise unchanged: same checksum reconstruction, same `ErrorCode.InvalidMnemonic` returns.

### `MnemonicService.ToSeed`

Body changes:
- Validate first (unchanged control flow; uses new `Validate(RecoveryPhrase)` overload).
- Compute total joined length: `totalChars = (phrase.Count - 1) /* spaces */ + Σ phrase[i].Length`.
- Rent a `char[]` from `ArrayPool<char>.Shared` of size `totalChars`.
- Concatenate into the rented buffer: for `i = 0..phrase.Count-1`, write a space at `pos++` if `i > 0`, then `phrase[i].CopyTo(charBuf.AsSpan(pos, phrase[i].Length))`, then `pos += phrase[i].Length`.
- Compute UTF-8 byte length and rent a `byte[]` from `ArrayPool<byte>.Shared`. For BIP-39 English all chars are ASCII, so byte length == char length; defensive: use `Encoding.UTF8.GetByteCount(charBuf.AsSpan(0, totalChars))` to size correctly.
- Encode UTF-8: `byteCount = Encoding.UTF8.GetBytes(charBuf.AsSpan(0, totalChars), byteBuf)`.
- Run PBKDF2: `Rfc2898DeriveBytes.Pbkdf2(byteBuf.AsSpan(0, byteCount), salt: "mnemonic"u8, iterations: 2048, hashAlgorithm: HashAlgorithmName.SHA512, outputLength: 64)`.
- In `finally`: `Array.Clear(charBuf, 0, totalChars); Array.Clear(byteBuf, 0, byteCount); ArrayPool<char>.Shared.Return(charBuf); ArrayPool<byte>.Shared.Return(byteBuf);`. Clearing before return is mandatory — pooled buffers may be handed to another caller next.

**NFKD normalization:** the BIP-39 spec calls for NFKD normalization before PBKDF2. The English wordlist is restricted to lowercase ASCII (a-z), which is already in NFKD canonical form — normalization is a bit-identity transform on this input. We skip the explicit `Normalize` call (which would force a `string` allocation we can't zero) and document the choice with an inline comment citing the wordlist's ASCII restriction. The existing `ToSeed_Bip39AllZerosVector_ProducesKnownSeedPrefix` test verifies bit-identity against the canonical BIP-39 test vector; if normalization mattered the test would fail.

### `MnemonicService` static initialization changes

```csharp
private static readonly string[] _wordlistArray = LoadWordlist();
private static readonly FrozenDictionary<string, int> _wordIndex = BuildWordIndex(_wordlistArray);
private static readonly FrozenDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _wordIndexSpan =
    _wordIndex.GetAlternateLookup<ReadOnlySpan<char>>();

private static FrozenDictionary<string, int> BuildWordIndex(string[] words)
{
    var dict = new Dictionary<string, int>(words.Length, StringComparer.Ordinal);
    for (var i = 0; i < words.Length; i++) { dict[words[i]] = i; }
    return dict.ToFrozenDictionary(StringComparer.Ordinal);
}
```

`AlternateLookup<ReadOnlySpan<char>>` requires the comparer to implement `IAlternateEqualityComparer<ReadOnlySpan<char>, string>`. `StringComparer.Ordinal` does (added in .NET 9).

### `FlashSkinkVolume.CreateAsync` — control-flow change at the return path

Existing structure between [line 166](src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:166) and the success return at [line 189](src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:189) becomes:

```csharp
var mnemonicResult = mnemonicService.Generate();
if (!mnemonicResult.Success)
{
    return Result<VolumeCreationReceipt>.Fail(mnemonicResult.Error!);
}

var phrase = mnemonicResult.Value!;
var phraseOwned = true;
try
{
    var seedResult = await SeedInitialSettingsAsync(connection, ct).ConfigureAwait(false);
    if (!seedResult.Success)
    {
        return Result<VolumeCreationReceipt>.Fail(seedResult.Error!);
    }

    // … take ownership of dek/connection (existing code, unchanged) …

    var session = new VolumeSession(ownedDek, ownedConnection);
    var volume = await BuildVolumeFromSessionAsync(session, skinkRoot, options,
        streamManager, lifecycle, keyVault, vaultPath).ConfigureAwait(false);

    phraseOwned = false;  // ownership transfers to receipt
    return Result<VolumeCreationReceipt>.Ok(new VolumeCreationReceipt(volume, phrase));
}
finally
{
    if (phraseOwned) { phrase.Dispose(); }
}
```

Compensation rule (Principle 17 `CancellationToken.None` literal pattern): unchanged — disposal of `phrase` is synchronous and does not involve cancellation; no `CancellationToken` parameter to mishandle.

Outer `OperationCanceledException`/`Exception` catches at [lines 191-198](src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:191) update their generic param: `Result<FlashSkinkVolume>.Fail(...)` → `Result<VolumeCreationReceipt>.Fail(...)`. If the OCE / Exception fires between `mnemonicResult` assignment and the success return, the inner `finally` zeroes the phrase before the outer catch runs.

`SeedInitialSettingsAsync` signature loses the `string[] mnemonicWords` parameter. The body drops the `RecoveryPhrase` upsert at [line 993](src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:993). The other four upserts (`GracePeriodDays`, `AuditIntervalHours`, `VolumeCreatedUtc`, `AppVersion`) and the catch blocks are unchanged.

## Integration points

### Callers of `FlashSkinkVolume.CreateAsync`

- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs` — ~42 call sites; mechanical `.Value!` → `.Value!.Volume`. The named-local variants at lines 80, 91, 120, 626, 634 also dereference `.Volume` on the named local. The receipt is consumed by `using var volume = (await ...).Value!.Volume;` — the phrase is implicitly leaked across the test's lifetime, which is acceptable for tests but documented as such in the rewritten `CreateAsync_NewSkinkRoot_GeneratesRecoveryPhrase` test which explicitly disposes the phrase.
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs:75` — 1 site.

There are no production callers — the CLI doesn't exist yet.

### Callers of `MnemonicService.{Generate,Validate,ToSeed}`

- `src/FlashSkink.Core/Crypto/KeyVault.cs:148` — `_mnemonic.ToSeed(words)` inside `UnlockFromMnemonicAsync`. The parameter name `words` is the `string[]` from the existing parameter; after this PR the `UnlockFromMnemonicAsync` parameter itself becomes `RecoveryPhrase phrase` and the call becomes `_mnemonic.ToSeed(phrase)`.
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs:166` — `mnemonicService.Generate()` already; just consumes the new `Result<RecoveryPhrase>` shape.
- `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs` — rewritten end-to-end.
- `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs:300` — `_mnemonic.ToSeed(words)` becomes `_mnemonic.ToSeed(phrase)` after wrapping `words` via `RecoveryPhrase.FromUserInput(words).Value!`. `using` on the phrase.

### CLI consumers (not in this PR)

For visibility: the future CLI `setup`-command handler will (a) check `IsOutputRedirected` / `IsInputRedirected` before calling `CreateAsync`, (b) await the receipt, (c) display `receipt.RecoveryPhrase` in alt-screen mode, (d) require user confirmation, (e) dispose `receipt.RecoveryPhrase`, (f) hand the volume to whatever next step needs it. None of that lives here.

## Principles touched

- **Principle 1** — Core never throws across its public API boundary. `RecoveryPhrase` deliberately deviates: post-dispose `Count` and indexer throw `ObjectDisposedException`. Rationale documented in the type's XML comment and in this plan; aligned with .NET conventions for `IDisposable` value containers (Stream, SqliteConnection, SemaphoreSlim). Factory methods (`Generate`, `FromUserInput`) return `Result<RecoveryPhrase>` per the principle.
- **Principle 6** — Zero-knowledge at every external boundary. Tightened: phrase no longer in brain mirror shipped to tails.
- **Principle 16** — Every failure path disposes partially-constructed resources. The `CreateAsync` inner try/finally and `FromUserInput`'s partial-allocation cleanup both exercise this.
- **Principle 18** — Allocation-conscious hot paths. `MnemonicService.Generate` is *not* a hot path (called once at setup), but `ToSeed` uses pooled buffers anyway — it's called in the password-reset / recovery flows where allocation pressure is irrelevant, but the pooling pattern also gives us the cleanup hook we need to zero buffers before returning to the pool.
- **Principle 22** — Brain hot paths use raw `SqliteDataReader`. Not relevant to this PR but mentioned because `SeedInitialSettingsAsync` uses Dapper — that's correct (cold setup path), unchanged.
- **Principle 26** — Logging never contains secrets. The plan removes a misleading comment that cited P26 as justification for persistence. P26 itself is unaffected; no logging changes.
- **Principle 31** — Keys are zeroed on volume close. The list (DEK, KEK, brain key, password buffer, decrypted OAuth tokens) expands to include "Recovery phrase char[][] buffers (zeroed on `RecoveryPhrase.Dispose`)". Blueprint §18.6 updated accordingly.

Principle deviations declared above: P1 (sanctioned exception for `RecoveryPhrase`'s post-dispose members, following the established .NET IDisposable convention and the existing precedent of `BrainConnectionFactory` returning a raw `SqliteConnection` wrapped in `Result`).

## Test spec

### `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs` (rewritten)

Helper for tests:
```csharp
private static RecoveryPhrase PhraseFrom(string[] words)
    => RecoveryPhrase.FromUserInput(words).Value!;
```

Tests (all existing + 1 new on the new type, names preserved where the semantics carry over):
- `Generate_Succeeds` — `using var phrase = _sut.Generate().Value!; Assert.NotNull(phrase);`
- `Generate_Returns24Words` — `using var phrase = _sut.Generate().Value!; Assert.Equal(24, phrase.Count);`
- `Generate_AllWordsAreInBip39Wordlist` — iterate `phrase`, assert each `phrase[i].ToString()` is in `MnemonicService.Wordlist`. (The `.ToString()` in tests is acceptable — tests aren't security-sensitive.)
- `Generate_TwoCallsProduceDifferentMnemonics` — same shape; compare via per-index `SequenceEqual` over the two phrases.
- `Validate_WithGeneratedMnemonic_Succeeds`
- `Validate_WrongWordCount_ReturnsInvalidMnemonic` — phrase from a 23-word `string[]`.
- `Validate_UnknownWord_ReturnsInvalidMnemonic` — phrase from a 24-word `string[]` with one word replaced by `"xyzzy"`.
- `Validate_BadChecksum_ReturnsInvalidMnemonic` — phrase from the all-zeros vector with last word changed to `"able"`.
- `Validate_KnownVector_Succeeds` — phrase from `AllZerosMnemonic`.
- `ToSeed_Returns64Bytes`
- `ToSeed_IsDeterministic`
- `ToSeed_DifferentMnemonics_ProduceDifferentSeeds`
- `ToSeed_Bip39AllZerosVector_ProducesKnownSeedPrefix` — **load-bearing** test: verifies algorithmic equivalence to the pre-rework `string`-based path. Same expected prefix `408b285c12383600`.
- `ToSeed_InvalidWords_ReturnsInvalidMnemonic`

New tests on `RecoveryPhrase` directly (added to a new file `tests/FlashSkink.Tests/Crypto/RecoveryPhraseTests.cs`):
- `FromUserInput_NullList_ReturnsInvalidMnemonic`
- `FromUserInput_NullWord_ReturnsInvalidMnemonic`
- `FromUserInput_ValidWords_ReturnsPhraseWithMatchingContent` — assert `phrase[i].SequenceEqual(words[i])` for each `i`.
- `Dispose_ZeroesUnderlyingBuffers` — use reflection (`typeof(RecoveryPhrase).GetField("_words", BindingFlags.NonPublic | BindingFlags.Instance)`) to grab a reference to the `char[][]` *before* `Dispose`, dispose, then assert each `char[]` is all-zero. This is a white-box test but it's the only way to verify the security-critical behaviour; documented in the test method's XML comment.
- `Dispose_IsIdempotent` — call `Dispose()` twice; second call doesn't throw.
- `Count_AfterDispose_ThrowsObjectDisposed`
- `Indexer_AfterDispose_ThrowsObjectDisposed`

`RecoveryPhraseTests.cs` ~80 lines.

### `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs`

- `CreateAsync_NewSkinkRoot_GeneratesRecoveryPhrase` rewritten:
  ```csharp
  var receipt = (await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions)).Value!;
  try
  {
      await receipt.Volume.DisposeAsync();
      Assert.Equal(24, receipt.RecoveryPhrase.Count);
      var wordSet = MnemonicService.Wordlist.ToHashSet(StringComparer.Ordinal);
      for (var i = 0; i < 24; i++)
      {
          Assert.Contains(receipt.RecoveryPhrase[i].ToString(), wordSet);
      }
  }
  finally
  {
      receipt.RecoveryPhrase.Dispose();
  }
  ```
- `CreateAsync_NewSkinkRoot_SeedsInitialSettings` — unchanged behaviour; only the `.Value!` → `.Value!.Volume` rewrite. (The four remaining seeded rows are unaffected.)
- **New test** `CreateAsync_DoesNotPersistRecoveryPhrase`:
  - Create the volume, dispose `.Volume` and `.RecoveryPhrase`.
  - Reopen the brain (the existing helper `ReadBrainSettingAsync` already does this).
  - `var row = await ReadBrainSettingAsync("RecoveryPhrase"); Assert.Null(row);`
  - Rationale comment cites blueprint §18.8 and this plan.
- All other call sites: `.Value!` → `.Value!.Volume` mechanical rewrite. Their tests don't touch the phrase; leaking it across the test scope is acceptable (xUnit will GC at test end; tests are not security-sensitive).

### `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs`

- Line 300 site rewritten: `using var phrase = RecoveryPhrase.FromUserInput(words).Value!; var seedResult = _mnemonic.ToSeed(phrase);`.

### `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs`

- Line 75 mechanical rewrite.

## Acceptance criteria

- [ ] `dotnet build` succeeds with zero warnings on `ubuntu-latest` and `windows-latest`.
- [ ] `dotnet test` is fully green.
- [ ] `ToSeed_Bip39AllZerosVector_ProducesKnownSeedPrefix` passes — proves algorithmic equivalence to the pre-rework path.
- [ ] `CreateAsync_DoesNotPersistRecoveryPhrase` passes.
- [ ] `RecoveryPhraseTests.Dispose_ZeroesUnderlyingBuffers` passes — proves zeroization is real, not just a Dispose call into a no-op.
- [ ] `Grep "RevealRecoveryPhraseAsync"` over `src/`, `tests/`, `BLUEPRINT.md`, `dev-plan/` returns zero matches.
- [ ] `Grep "Settings\[\"RecoveryPhrase\"\]"` over `src/` returns zero matches.
- [ ] `flashskink-cli reveal-phrase` removed from §25 CLI surface listing.
- [ ] §29 row A16 reads `(a) No re-display; caller must record at setup`.
- [ ] §18.6 keys-zeroed list includes recovery-phrase char[][] buffers.
- [ ] `dotnet format --verify-no-changes` is clean.

## Line-of-code budget

- `src/FlashSkink.Core.Abstractions/Crypto/RecoveryPhrase.cs` — ~90 lines (new).
- `src/FlashSkink.Core.Abstractions/Models/VolumeCreationReceipt.cs` — ~30 lines (new).
- `src/FlashSkink.Core/Crypto/MnemonicService.cs` — net ~+30 lines (signatures + pooled-buffer `ToSeed` + FrozenDictionary).
- `src/FlashSkink.Core/Crypto/KeyVault.cs` — ~5 line delta.
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — ~30 line delta (signature + try/finally for phrase disposal + drop persistence).
- `tests/FlashSkink.Tests/Crypto/MnemonicServiceTests.cs` — ~50 lines net (existing tests get +1-2 lines each for `using`).
- `tests/FlashSkink.Tests/Crypto/RecoveryPhraseTests.cs` — ~80 lines (new file).
- `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs` — ~3 line delta.
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs` — ~60 lines net.
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs` — 1 line delta.
- Blueprint + dev-plan edits — ~25 lines net (mostly deletions).
- Total: ~400 lines added, ~30 deleted from `src/`. Net ~370 lines added.

## Non-goals

- **Do NOT introduce mnemonic-at-rest encryption.** Persistence is being deleted, not encrypted.
- **Do NOT implement `RevealRecoveryPhraseAsync` as a stub.** The API is being removed end-to-end.
- **Do NOT add a finalizer to `RecoveryPhrase`.** Disposal is the contract; finalizer-based zeroing gives false security and races GC.
- **Do NOT make `VolumeCreationReceipt` itself `IDisposable`.** The volume and the phrase have different lifetimes and should be disposed separately.
- **Do NOT touch CLI display logic** — `Console.IsOutputRedirected` checks, alt-screen mode, "I've recorded it" confirmation. All belong in the future CLI `setup`-command PR. They're listed in this plan only for the eventual CLI implementer's reference.
- **Do NOT touch Decision A17 (password reset via mnemonic).** Reset still works — the user types the phrase into the CLI at reset time, the CLI wraps it via `RecoveryPhrase.FromUserInput`, and `KeyVault.UnlockFromMnemonicAsync(...,  RecoveryPhrase)` consumes it. A17 stays `(a) Fully supported`.
- **Do NOT touch the BIP-39 spec compliance of `ToSeed`.** Skipping NFKD for the English wordlist is correct per the spec for ASCII input; the all-zeros test vector verifies bit-identity.
- **Do NOT bundle Refactor PR A** (volume identity + version metadata). This PR pushes first; Refactor PR A rebases on top.
- **Do NOT touch `FrozenSet<string> _wordlistSet`** (currently in `MnemonicService` — read [MnemonicService.cs:17](src/FlashSkink.Core/Crypto/MnemonicService.cs:17)). It's unused after this PR (Validate no longer needs a set membership check separate from the index lookup). Leaving it as dead code would violate the project's "no dead code" lean — it's deleted as part of the rework. (Documented separately here because it's a small additional change easy to miss.)
