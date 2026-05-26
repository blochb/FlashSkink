# Refactor — `Result`/`Result<T>` flow attributes + null-forgiving sweep

**Branch:** refactor/result-flow-attributes
**Base:** main (post-§4.4 merge, cd1d035)
**Blueprint sections:** §6.1 (Result pattern), §6.5 (catch-block ordering)
**Dev plan section:** none — cross-cutting cleanup, not tied to a phase
**Plan-filename convention (Gate 1 decision):** non-dev-plan plans use
`refactor-<slug>.md` or `chore-<slug>.md` to mirror `pr-X.Y.md`. This file
follows the new convention. Add the convention to CLAUDE.md's "Quick
reference / Where things live" section as part of this PR.

## Scope

Two halves, shipping together because the second motivates the first:

1. **Mechanical:** add `[MemberNotNullWhen]` flow attributes to `Result` and `Result<T>`
   so the nullable analyzer learns the `Success` ↔ `Value`/`Error` correlation, then
   sweep every `.Value!` / `.Error!` that becomes redundant. Survey count at the time
   of writing: 896 sites across ~70 files (roughly half production, half tests).

2. **Process:** promote the CLAUDE.md convention *"No `!` suppression without a
   comment explaining why"* to a numbered principle, with stronger wording that
   distinguishes "type-author should fix this" from "invariant genuinely outside
   the type system." Update `principle-audit.yml`'s prompt to scan for it.

The reason both halves ship together is causal: the new principle exists because
the old convention compounded undetected across a dozen PRs. Documenting the rule
without sweeping the existing damage would leave new contributors (human or LLM)
reading 800+ counter-examples in the codebase.

## Files to create

- `tests/FlashSkink.Tests/ResultAssertions.cs` — small test-only helper
  centralizing the "assert Success, then consume Value/Error" pattern (added
  during implementation after the sweep surfaced ~200 test sites where
  removing `!` broke the build). Concentrates a single `!` per overload
  inside the helper, behind an `Assert.True(result.Success)` precondition
  that throws if the invariant is violated. The principle is preserved —
  the invariant is encoded once in the helper rather than scattered as
  `!` across every test. ~50 lines.

  API:
  ```csharp
  internal static T AssertValue<T>(this Result<T> r)
  internal static ErrorContext AssertError<T>(this Result<T> r)
  internal static ErrorContext AssertError(this Result r)
  ```

## Files to modify

- `src/FlashSkink.Core.Abstractions/Results/Result.cs` — add
  `[MemberNotNullWhen(false, nameof(Error))]` on `Success`. ~3 lines.
- `src/FlashSkink.Core.Abstractions/Results/ResultOfT.cs` — add
  `[MemberNotNullWhen(true, nameof(Value))]` and `[MemberNotNullWhen(false, nameof(Error))]`
  on `Success`; change `T? Value { get; }` to `T Value { get; }` with `[MaybeNull]`.
  Add `using System.Diagnostics.CodeAnalysis;`. ~6 lines.
- Every consumer file under `src/` and `tests/` with `.Value!` or `.Error!`
  immediately after a `Success`/`!Success` guard. Mechanical sweep — remove the
  `!`. The analyzer enforces correctness: if removing `!` triggers CS8602, the
  guard is missing or the access is genuinely unguarded (a real bug or a site
  where `!` is correctly retained with a comment).
- `CLAUDE.md` — add Principle 37 (proposed wording below); update the
  "Conventions / C# style" bullet so the old line points to the new principle
  instead of restating it.
- `.github/workflows/ci.yml` — extend the `principle-audit` prompt to mention
  the new principle and scan for `!` reachable from a `Success`/`!Success`
  branch (LLM-judgment, not regex).

Sweep estimate from the survey: ~25 production files, ~45 test files. Diff will
be large but per-site change is trivial (delete one character).

## Dependencies

None. `System.Diagnostics.CodeAnalysis` is part of `netstandard2.0+` and already
implicitly imported under `<ImplicitUsings>enable</ImplicitUsings>` for the SDK
attribute set — but `[MemberNotNullWhen]` and `[MaybeNull]` need an explicit
`using` since they're not in the implicit-usings list. Add the using to both
files explicitly to avoid relying on transitive imports.

## Public API surface

### `FlashSkink.Core.Abstractions.Results.Result` (readonly record struct) — modified

```csharp
[MemberNotNullWhen(false, nameof(Error))]
public bool Success { get; }

public ErrorContext? Error { get; }
```

Summary intent: when `Success` is `false`, `Error` is guaranteed non-null. The
analyzer carries this guarantee through `if (!result.Success)` and pattern
matches. No behavior change at runtime; the only effect is on nullable
analysis at consumer call sites.

### `FlashSkink.Core.Abstractions.Results.Result<T>` (readonly record struct) — modified

```csharp
[MemberNotNullWhen(true, nameof(Value))]
[MemberNotNullWhen(false, nameof(Error))]
public bool Success { get; }

[MaybeNull]
public T Value { get; }

public ErrorContext? Error { get; }
```

Summary intent: when `Success` is `true`, `Value` is guaranteed non-null; when
`false`, `Error` is. `[MaybeNull]` on `Value` (combined with dropping the `T?`)
makes the property usable as `T` at success-checked call sites without a
nullable-suppress; the analyzer treats it as maybe-null elsewhere.

### `Result.Fail(...)`, `Result<T>.Ok(...)`, `Result<T>.Fail(...)`

Unchanged signatures, unchanged behavior.

## Internal types

None added or modified.

## Method-body contracts

No method bodies change. The factories already establish the invariants
(`Ok` returns `(true, value, null)`, `Fail` returns `(false, default, error)`).
The attributes describe invariants already true at runtime; this PR only
makes them visible to the analyzer.

## Integration points

Every caller of `Result.Fail`, `Result<T>.Ok`, `Result<T>.Fail`, and every
reader of `.Success`, `.Value`, `.Error`. Signatures are stable; the only
caller-visible change is *removing* a previously-required `!` after a
`Success` check.

Two patterns the sweep must NOT mistakenly remove:

1. `result.Error!.Code` where there is **no** `if (!result.Success)` guard
   in scope — e.g. inside an exception-handler that constructs an error from
   a Result already known to be failed via a different route. Each such site
   needs human/LLM judgment: keep the `!` and add a comment per the new
   principle, or refactor to make the precondition visible.

2. `result.Value` access on a `Result<int>`, `Result<long>`, `Result<bool>`,
   etc. (value-typed `T`). With `T = long`, `Value` is now typed `long`
   (not `long?`) and the success check is unnecessary for null-safety —
   but still semantically required (you can't read `Value` from a failed
   result). The sweep removes the `!`; the `Success` guard stays.

   The one specifically problematic case is `Result<T?>` for nullable
   value-type `T?` (e.g. `Result<long?>`). The attribute can't unwrap
   `Nullable<T>`; callers still need to handle the inner nullability.
   Grep confirms we have **zero** such cases in the codebase today, so it
   doesn't gate this PR — but the new principle should call it out so
   future authors don't expect magic.

## Principles touched

- Principle 1 (Core never throws across its public API) — `Result`/`Result<T>`
  are the load-bearing types; this change strengthens their type-level
  guarantees without altering the contract.
- The conventions bullet on `!` suppression — promoted to Principle 37
  (see proposed text below).
- Principle 23 (Provider contract is frozen before V1 ships) — `Result<T>` is
  not part of `IStorageProvider` but is the return type of every provider
  method. The shape change (`T?` → `T` with `[MaybeNull]`) is source-level
  forward-compatible for all current consumers; the only sites that break are
  sites that *would have failed compilation anyway* without `!`. Provider
  authors writing against `Result<T>.Value` directly will see exactly the
  improvement the sweep delivers. Acceptable under "additive-only" if we
  classify attribute-only metadata changes as additive.

### Proposed Principle 37 wording (for CLAUDE.md)

> **37. Encode invariants in the type system before reaching for `!`.** When the
> nullable analyzer can't see an invariant, the first question is whether the
> *type author* can teach it via flow attributes (`[MemberNotNullWhen]`,
> `[NotNullWhen]`, `[MaybeNullWhen]`, `[MaybeNull]`, `[NotNull]`) — not whether
> the *call site* can paper over it with `!`. `!` to silence the analyzer at a
> call site for a correlation the type itself could express is a defect at the
> type, not the call site, and is a Gate 2 rejection. `!` is acceptable only
> when the invariant exists but cannot be encoded — e.g. inside a `catch ...
> when (...)` filter that already proved the access path non-null, or when
> bridging a third-party API whose type signatures lie. Every accepted `!`
> carries a comment naming the invariant (one line, no prose). The reference
> implementation is `Result<T>` with `[MemberNotNullWhen]` carrying the
> `Success` ↔ `Value`/`Error` correlation. (Blueprint §6.1; PR
> `refactor/result-flow-attributes`.)

**Gate 1 decision:** ship as Principle 37 (numbered, audited by
`principle-audit.yml`). The failure mode this PR is correcting — a convention
quietly compounding into 800+ counter-examples — is exactly the kind of drift
unenforced conventions invite. Numbered principles are the lever that
mechanically applies to every future PR.

## Test spec

No new test files. Existing tests verify Result behavior; this PR doesn't
change behavior, only annotations.

**One new architectural assertion** added to the existing
`tests/FlashSkink.Tests/Architecture/ArchitectureTests.cs` (Gate 1 decision —
the file is verified to exist before implementation via Glob; if it doesn't,
the assertion lands there as a new file in the same folder where the
principles 8–11 layering test lives).

The reflection target deliberately uses `typeof(Result<object>)` rather than
the open generic. Reading attributes from a *closed* generic instantiation is
straightforward and analyzer-friendly, while `typeof(Result<>).GetProperty(...)`
on an open generic forces the reader to reason about whether attribute
metadata flows through type-parameter substitution. `Result<object>` is the
simplest closed binding and carries the same attributes the runtime emits for
every instantiation.

```csharp
[Fact]
public void Result_Success_Property_Carries_MemberNotNullWhen_Attributes()
{
    // Validates Principle 37 by reflection so it can't silently regress.
    var resultSuccess = typeof(Result).GetProperty(nameof(Result.Success))!;
    Assert.Contains(
        resultSuccess.GetCustomAttributes(typeof(MemberNotNullWhenAttribute), inherit: false)
                     .Cast<MemberNotNullWhenAttribute>(),
        a => a.ReturnValue == false && a.Members.Contains(nameof(Result.Error)));

    var resultOfTSuccess = typeof(Result<object>).GetProperty(nameof(Result<object>.Success))!;
    var attrs = resultOfTSuccess.GetCustomAttributes(typeof(MemberNotNullWhenAttribute), inherit: false)
                                .Cast<MemberNotNullWhenAttribute>()
                                .ToArray();
    Assert.Contains(attrs, a => a.ReturnValue == true  && a.Members.Contains(nameof(Result<object>.Value)));
    Assert.Contains(attrs, a => a.ReturnValue == false && a.Members.Contains(nameof(Result<object>.Error)));
}
```

## Acceptance criteria

- [ ] Both `Result.cs` and `ResultOfT.cs` carry the flow attributes.
- [ ] `git grep -cE '\.(Value|Error)!' src tests` drops from 896 to a small
      residue (estimate: under 50, all carrying the per-principle comment).
- [ ] Every retained `!` carries a one-line comment naming the invariant.
- [ ] Builds with zero warnings on both `ubuntu-latest` and `windows-latest`.
- [ ] All existing tests pass — zero behavior change expected.
- [ ] New architectural reflection test passes.
- [ ] CLAUDE.md carries Principle 37 (numbered + audited; Gate 1 locked).
- [ ] CLAUDE.md's "Quick reference" or "Branch naming" section documents
      the `refactor-<slug>.md` / `chore-<slug>.md` plan-filename convention.
- [ ] `principle-audit.yml` prompt mentions Principle 37.
- [ ] `pr-review.yml` / `pr-review-crypto.yml` / `pr-review-recovery.yml`
      prompts unchanged (this is principle-track, not general-review track).

## Line-of-code budget

- `Result.cs` — +3 lines (using + attribute).
- `ResultOfT.cs` — +5 lines (using + 2 attributes; `T?` → `T` + `[MaybeNull]`).
- Sweep across ~70 files — net deletion, roughly 850 character-level changes
  (one `!` per site), zero new lines. Diff line count will look large because
  every touched line shows as a change, but each line shrinks by one char.
- `CLAUDE.md` — +15 lines (Principle 37 + cross-reference cleanup).
- `ci.yml` principle-audit prompt — +2 lines.
- Architecture test — +25 lines.
- **Total non-test:** ~25 lines added, ~850 character-deletions.
- **Total test:** ~25 lines added.

## Non-goals

- Do NOT change `Result<T>`'s factory methods, constructor, or runtime
  behavior. Attribute-only metadata change.
- Do NOT touch the Dropbox-exception-pattern `!`s
  (`aex.ErrorResponse!.AsPath!.Value` etc.). Those are a different smell with
  a different fix (pattern-bind in the `when` clause). A follow-up PR
  `refactor/dropbox-exception-pattern-binding` handles them — adding it to
  Gate 1's discussion is welcome but not required.
- Do NOT sweep `!` on non-Result types (collection nulls, deserialization,
  reflection results, etc.). Those are individually examined; this PR is
  narrowly Result-shaped.
- Do NOT modify the provider interface, brain schema, or any blueprint-level
  contract. This PR is purely an analyzer-visibility improvement.
- Do NOT introduce a regex-based pre-commit hook for `!` detection. The
  principle-audit prompt is the chosen enforcement; mechanical regex tripped
  on too many false positives during a paper-test (e.g. `bool!.ToString()`
  in unrelated code).
- Do NOT split into multiple PRs. The whole point is to ship the type fix
  and the sweep together so reviewers see one coherent change. Splitting
  would land 70 small PRs that each say "removed one `!`" with no context.

## Implementation order

1. Modify `Result.cs` and `ResultOfT.cs`.
2. `dotnet build` — every `.Value` access on a value-typed `Result<T>` may
   now compile cleanly without `!`; everywhere flow analysis sees the guard,
   `!` becomes redundant (a warning, not an error). Capture the warning list.
3. Walk the warning list file by file, deleting `!`s. Compile after each
   directory to catch any mis-removal.
4. Add the architectural test.
5. Update CLAUDE.md with Principle 37.
6. Update `ci.yml` principle-audit prompt.
7. `dotnet test` — green.
8. `dotnet format --verify-no-changes` — clean.
9. Commit, push, PR.

## Gate 1 decisions (locked)

1. **Principle vs. convention** → **Principle 37**, numbered and audited by
   `principle-audit.yml`. Failure-mode parity with Principles 26/28.
2. **Plan-filename convention for non-dev-plan PRs** → `refactor-<slug>.md`
   and `chore-<slug>.md`. Document in CLAUDE.md as part of this PR.
3. **Provider contract additivity (Principle 23) interpretation** →
   attribute-only metadata changes count as additive. `Result<T>`'s runtime
   shape (`T Value { get; }` returning the same IL) is unchanged; the only
   caller-visible effect is the analyzer learning a previously-invisible
   invariant. Source-compatible for every current consumer.
4. **Architectural test placement** → `tests/FlashSkink.Tests/Architecture/`,
   alongside the existing principle-8–11 layering test. Glob-verify the
   directory exists at Step 1 of implementation; if the file isn't there,
   create it as a new file in the same directory.

## Side note — the reflection-test snippet

The original draft used `typeof(Result<>).GetProperty(nameof(Result<int>.Success))!`,
which mixed an open generic (the property holder) with a closed generic just
to satisfy `nameof` and tripped the analyzer through a null-forgiving on the
return of `GetProperty`. Both problems disappear with `typeof(Result<object>)`
as a single closed instantiation: the property lookup succeeds without `!`
because `Success` exists on the closed type, and `nameof(Result<object>.Value)`
/ `nameof(Result<object>.Error)` use the same closed binding consistently.
Closed bindings carry the same `MemberNotNullWhen` metadata as every other
instantiation — there's no information loss.
