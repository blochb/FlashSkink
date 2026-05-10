# PR 3.2 — Retry policy and backoff

**Branch:** `pr/3.2-retry-policy-and-backoff`
**Blueprint sections:** §21.1 (provider failure during upload — full), §15.3 (step 6 failure handling — the per-range failure branch the policy serves), §22.1 (health-affecting backoff — explicitly *not* implemented here; documented as Phase 5)
**Dev plan section:** `dev-plan/phase-3-Upload-queue-and-resumable-uploads.md` §3.2

## Scope

Phase 3 §3.2 introduces two small, foundational pieces that §3.3 (RangeUploader) and §3.4 (UploadQueueService) consume:

1. **A clock abstraction** (`IClock` + `SystemClock` in `Core.Abstractions/Time/`, plus a test-only `FakeClock` in `tests/_TestSupport/`). Every later Phase 3 PR routes time-dependent operations — retry waits, the orchestrator's idle poll, the brain-mirror 15-minute timer, the upload-session debounce — through this seam so tests can advance time deterministically.

2. **A pure retry-policy state machine** (`RetryPolicy` + `RetryDecision` + `RetryOutcome` in `Core/Upload/`). `RetryPolicy` encodes the §21.1 ladder exactly: 1 s / 4 s / 16 s for in-range attempts 1/2/3, then `EscalateCycle` at attempt 4; 5 min / 30 min / 2 h / 12 h for cycle attempts 1/2/3/4, then `MarkFailed` at cycle 5. The policy is a pure function — no I/O, no async, no `IClock` — so the *consumer* (RangeUploader for the in-range ladder, UploadQueueService for the cycle ladder) decides when to actually wait.

No upload code is wired in this PR. After it merges, §3.3 imports `RetryPolicy` and `IClock`; §3.4 imports both as well; §3.5's brain-mirror timer imports `IClock`.

## Files to create

### `src/FlashSkink.Core.Abstractions/Time/`

- `IClock.cs` — public interface with `DateTime UtcNow { get; }` and `ValueTask Delay(TimeSpan delay, CancellationToken ct)`. ~35 LOC including XML docs.
- `SystemClock.cs` — public sealed default implementation. `UtcNow → DateTime.UtcNow`; `Delay(d, ct) → new ValueTask(Task.Delay(d, ct))`. ~25 LOC.

### `src/FlashSkink.Core/Upload/`

- `RetryOutcome.cs` — public enum `Retry | EscalateCycle | MarkFailed`. ~20 LOC.
- `RetryDecision.cs` — public `readonly record struct` with `RetryOutcome Outcome` and `TimeSpan Delay`; static factory shortcuts. ~45 LOC.
- `RetryPolicy.cs` — public sealed class encoding the §21.1 ladder. Two methods: `RetryDecision NextRangeAttempt(int rangeAttemptNumber)` and `RetryDecision NextCycleAttempt(int cycleNumber)`. ~110 LOC including XML docs and the two `static readonly TimeSpan[]` ladders.

### `tests/FlashSkink.Tests/_TestSupport/`

- `FakeClock.cs` — `internal sealed class : IClock`. Exposes `Advance(TimeSpan)`, `UtcNow`, and a `PendingDelayCount` diagnostic. Each `Delay(d, ct)` call enrols a `(TaskCompletionSource, deadline)`; `Advance` completes any whose deadline `<= UtcNow + d`. Thread-safe via lock. ~140 LOC.

### `tests/FlashSkink.Tests/Upload/`

- `RetryPolicyTests.cs` — exhaustive coverage of the §21.1 ladder, both directions. ~220 LOC.

### `tests/FlashSkink.Tests/_TestSupport/`

- `FakeClockTests.cs` — exercises every fake-clock contract. ~180 LOC.

## Files to modify

None. No production wiring lands in this PR — that's §3.3 / §3.4.

## Dependencies

- **NuGet:** none new.
- **Project references:** none added. `Core.Abstractions/Time/` types reference no external project; `Core/Upload/RetryPolicy.cs` references no other project (uses `TimeSpan` only).

## Public API surface

### `FlashSkink.Core.Abstractions.Time.IClock` (interface)

Summary intent: synchronous time source plus cancellable delay. Consumers that want deterministic time in tests inject this in place of `DateTime.UtcNow` / `Task.Delay`.

```csharp
namespace FlashSkink.Core.Abstractions.Time;

public interface IClock
{
    /// <summary>Current UTC time. Sanctioned non-Result property: cannot fail (Principle 1, pure-function carve-out).</summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// Returns a task that completes after <paramref name="delay"/> elapses.
    /// Honours <paramref name="ct"/>: a cancelled token causes the returned <see cref="ValueTask"/>
    /// to complete with <see cref="OperationCanceledException"/>, identical to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="ValueTask"/> rather than <see cref="System.Threading.Tasks.Task"/> because hot paths (retry loops,
    /// idle waits) call this method per iteration. Cancellation flows through <see cref="OperationCanceledException"/>
    /// rather than a <see cref="Results.Result"/> return type — this is the same shape as the underlying <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// it abstracts; mapping to <c>Result.Fail(Cancelled)</c> happens at the caller's public boundary
    /// per Principle 14, exactly as for any other awaited primitive.
    /// </remarks>
    ValueTask Delay(TimeSpan delay, CancellationToken ct);
}
```

**Why `Delay` doesn't return `Result`:** Same reason `Task.Delay` doesn't. Cancellation is the only failure mode, and `OperationCanceledException` is the universal in-band carrier for it across all `await` boundaries in the codebase. Wrapping it in `Result` here would force every caller to write boilerplate to unwrap; the wrap is the *caller's* job at its own public boundary (Principle 14 catch-first pattern). This is consistent with how `Task.Delay`, `Stream.ReadAsync`, and `SqliteConnection.ExecuteAsync` are already consumed across the codebase — none of which return `Result` either; their callers wrap.

### `FlashSkink.Core.Abstractions.Time.SystemClock` (sealed class)

Summary intent: production `IClock` backed by the OS clock. Singleton-friendly (no state).

```csharp
namespace FlashSkink.Core.Abstractions.Time;

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public SystemClock() { }

    public DateTime UtcNow => DateTime.UtcNow;

    public ValueTask Delay(TimeSpan delay, CancellationToken ct) =>
        new(Task.Delay(delay, ct));
}
```

The `Instance` static is provided for callers that want a default singleton without wiring DI; production code in the upload pipeline injects `IClock` explicitly via constructor.

### `FlashSkink.Core.Upload.RetryOutcome` (enum)

```csharp
namespace FlashSkink.Core.Upload;

public enum RetryOutcome
{
    /// <summary>Caller should wait <see cref="RetryDecision.Delay"/> and retry the same operation.</summary>
    Retry = 0,

    /// <summary>In-range retry budget exhausted; caller should escalate to the cycle ladder
    /// (record a row-level failure and consult <see cref="RetryPolicy.NextCycleAttempt"/>).</summary>
    EscalateCycle = 1,

    /// <summary>Cycle retry budget exhausted; caller should mark the row FAILED and stop retrying.</summary>
    MarkFailed = 2,
}
```

### `FlashSkink.Core.Upload.RetryDecision` (`readonly record struct`)

```csharp
namespace FlashSkink.Core.Upload;

public readonly record struct RetryDecision(RetryOutcome Outcome, TimeSpan Delay)
{
    /// <summary>Wait <paramref name="delay"/> and retry.</summary>
    public static RetryDecision Wait(TimeSpan delay) => new(RetryOutcome.Retry, delay);

    /// <summary>In-range budget exhausted. <see cref="Delay"/> is <see cref="TimeSpan.Zero"/>.</summary>
    public static RetryDecision Escalate() => new(RetryOutcome.EscalateCycle, TimeSpan.Zero);

    /// <summary>Cycle budget exhausted. <see cref="Delay"/> is <see cref="TimeSpan.Zero"/>.</summary>
    public static RetryDecision Fail() => new(RetryOutcome.MarkFailed, TimeSpan.Zero);
}
```

A value type rather than a class because `RetryPolicy.Next*` is on the hot path (one allocation per failed range would be wasteful at fleet scale) and the data is two fields.

### `FlashSkink.Core.Upload.RetryPolicy` (sealed class)

Summary intent: pure encoding of blueprint §21.1's two retry ladders. No I/O, no async, no clock — the caller decides when to wait. Health-blind (Phase 5 modulates by re-entering or skipping the loop, not by changing the ladder).

```csharp
namespace FlashSkink.Core.Upload;

public sealed class RetryPolicy
{
    /// <summary>Default singleton; the policy is stateless.</summary>
    public static RetryPolicy Default { get; } = new();

    public RetryPolicy() { }

    /// <summary>
    /// Decide what to do after the in-range attempt numbered <paramref name="rangeAttemptNumber"/> failed.
    /// </summary>
    /// <param name="rangeAttemptNumber">1-based attempt counter — 1 means "this was the first attempt at the range".</param>
    /// <returns>
    /// For attempts 1/2/3: <see cref="RetryDecision.Wait"/> with the §21.1 in-range delay (1 s / 4 s / 16 s).
    /// For attempt 4 and beyond: <see cref="RetryDecision.Escalate"/>.
    /// </returns>
    /// <remarks>
    /// Pure function. Never throws (Principle 1 sanctioned exception for total functions over inputs).
    /// <paramref name="rangeAttemptNumber"/> &lt; 1 is treated as 1 (first attempt) — the caller's
    /// counter is the source of truth and clamping is a defence, not a contract violation.
    /// </remarks>
    public RetryDecision NextRangeAttempt(int rangeAttemptNumber);

    /// <summary>
    /// Decide what to do after cycle attempt <paramref name="cycleNumber"/> failed (i.e. an in-range
    /// ladder fully escalated for the <paramref name="cycleNumber"/>-th time on this row).
    /// </summary>
    /// <param name="cycleNumber">1-based cycle counter — 1 means "this row's first cycle just failed".</param>
    /// <returns>
    /// For cycles 1/2/3/4: <see cref="RetryDecision.Wait"/> with the §21.1 cycle delay (5 min / 30 min / 2 h / 12 h).
    /// For cycle 5 and beyond: <see cref="RetryDecision.Fail"/>.
    /// </returns>
    /// <remarks>Pure function. Never throws. Same clamping behaviour as <see cref="NextRangeAttempt"/>.</remarks>
    public RetryDecision NextCycleAttempt(int cycleNumber);
}
```

**Sanctioned-pure-function declaration:** Both `Next*` methods qualify for the Principle 1 carve-out (pure, total, no I/O, no allocation that could OOM). XML docs state "Never throws" explicitly.

## Internal types

None.

## Method-body contracts

### `RetryPolicy.NextRangeAttempt(int rangeAttemptNumber)`

- **Pre:** none — clamping handles invalid input.
- **Internal data:** `private static readonly TimeSpan[] RangeDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16)];`
- **Steps:**
  1. Compute `idx = Math.Max(0, rangeAttemptNumber - 1)`.
  2. If `idx < RangeDelays.Length`: return `RetryDecision.Wait(RangeDelays[idx])`.
  3. Else: return `RetryDecision.Escalate()`.
- **Errors:** none — pure function.

### `RetryPolicy.NextCycleAttempt(int cycleNumber)`

- **Pre:** none.
- **Internal data:** `private static readonly TimeSpan[] CycleDelays = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];`
- **Steps:**
  1. Compute `idx = Math.Max(0, cycleNumber - 1)`.
  2. If `idx < CycleDelays.Length`: return `RetryDecision.Wait(CycleDelays[idx])`.
  3. Else: return `RetryDecision.Fail()`.
- **Errors:** none.

### `SystemClock.Delay(TimeSpan delay, CancellationToken ct)`

- Returns `new ValueTask(Task.Delay(delay, ct))`.
- `Task.Delay` validates `delay`; we don't pre-validate — let the underlying primitive's contract govern.
- A pre-cancelled `ct` makes `Task.Delay` return a faulted task immediately; that propagates verbatim.

### `FakeClock` (test-only)

- **State:** `_now` (`DateTime`), `_pending` (sorted list of `(deadline, TaskCompletionSource, CancellationTokenRegistration)`), single `lock` object.
- **`UtcNow`:** returns `_now` (read under lock so concurrent `Advance` is safe).
- **`Delay(delay, ct)`:**
  1. If `delay <= TimeSpan.Zero`: return `ValueTask.CompletedTask`.
  2. If `ct.IsCancellationRequested`: return `ValueTask.FromCanceled(ct)`.
  3. Compute `deadline = _now + delay` under lock; create a `TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)`; insert sorted by deadline.
  4. Register a cancellation callback that removes the entry under lock and calls `tcs.TrySetCanceled(ct)`.
  5. Return `new ValueTask(tcs.Task)`.
- **`Advance(TimeSpan delta)`:**
  1. Under lock: compute `newNow = _now + delta`; collect all pending entries with `deadline <= newNow`; remove them from the list; set `_now = newNow`.
  2. Outside the lock: dispose each entry's `CancellationTokenRegistration`, then call `tcs.TrySetResult()` on each (in deadline order).
  3. The "outside the lock" sequencing matters: completing a `TaskCompletionSource` can run continuations synchronously even with `RunContinuationsAsynchronously` if a continuation was already attached as `ExecuteSynchronously`; we don't want any callback to re-enter `FakeClock` while we hold the lock.
- **`PendingDelayCount`:** read under lock; returns count for diagnostics.
- **`Dispose`:** cancel all pending TCSes via `TrySetCanceled()` (no token), dispose registrations.

## Integration points

This PR is consumed by — but does not call into — any existing type. It does:

- Reference `System.Threading.Tasks.ValueTask`, `System.Threading.CancellationToken`, `System.Threading.Tasks.TaskCompletionSource` — BCL only.
- Reference `Microsoft.Extensions.Logging` — *not* used by `RetryPolicy`, `IClock`, `SystemClock`, or `FakeClock`. None of these types log.

No volume-level wiring. No upload-pipeline wiring. No brain interaction. After this PR:

- §3.3 `RangeUploader` constructor takes `IClock clock, RetryPolicy retryPolicy` and uses both in its inner per-range retry loop.
- §3.4 `UploadQueueService` takes the same two and uses them in the cycle loop and the orchestrator/worker idle waits.
- §3.5 `BrainMirrorService` takes `IClock` for its 15-minute timer and the 10-second debounce.

## Principles touched

- **Principle 1** — `RetryPolicy.Next*` are sanctioned pure functions: total over their `int` input, no I/O, no allocation that could OOM, return value type. XML doc says "Never throws". `SystemClock.UtcNow` is a sanctioned property (cannot fail). `IClock.Delay` returns `ValueTask` rather than `Task<Result>` — cancellation flows via `OperationCanceledException` (Principle 14 — wrap-at-consumer-boundary, identical shape to `Task.Delay`). The XML remarks on `IClock.Delay` document the rationale so a Gate-2 reviewer doesn't pattern-match this as a Principle 1 violation.
- **Principle 8** — `IClock` and `SystemClock` live in `Core.Abstractions/Time/`; reference no UI/Presentation/Core. `RetryPolicy` lives in `Core/Upload/`; references no UI/Presentation.
- **Principle 12** — OS-agnostic. `Task.Delay`, `DateTime.UtcNow`, `TimeSpan` are cross-platform.
- **Principle 13** — `IClock.Delay` takes `CancellationToken ct` as its final parameter.
- **Principle 14** — `RetryPolicy` has no `try/catch`; `SystemClock.Delay` does not catch; `FakeClock.Delay` propagates cancellation via `ValueTask.FromCanceled` and the registered callback. No swallowed `OperationCanceledException` anywhere.
- **Principle 16** — `FakeClock` disposes all `CancellationTokenRegistration` instances on completion or on its own `Dispose`.
- **Principle 18** — `RetryDecision` is a `readonly record struct`. `RetryPolicy.Next*` is allocation-free (returns a value type; the `RangeDelays` and `CycleDelays` arrays are `static readonly` and never copied).
- **Principle 23** — adds new abstractions (`IClock`); does not modify existing ones (`IStorageProvider`, `UploadSession`, `INotificationBus`, etc. all unchanged).
- **Principle 26** — no logging; no secrets risk.
- **Principle 27** — no logging at all; no double-log risk.
- **Principle 28** — no logger references.

## Test spec

Naming: `Method_State_ExpectedBehavior` per established convention.

### `tests/FlashSkink.Tests/Upload/RetryPolicyTests.cs`

- `NextRangeAttempt_FirstAttempt_Returns1SecondWait`
- `NextRangeAttempt_SecondAttempt_Returns4SecondWait`
- `NextRangeAttempt_ThirdAttempt_Returns16SecondWait`
- `NextRangeAttempt_FourthAttempt_ReturnsEscalate`
- `NextRangeAttempt_HighAttemptNumber_ReturnsEscalate` (e.g. 100)
- `NextRangeAttempt_ZeroOrNegative_TreatedAsFirstAttempt` `[Theory]` over `0, -1, -100` → all `Wait(1s)`. Validates the clamping defence.
- `NextCycleAttempt_FirstCycle_Returns5MinuteWait`
- `NextCycleAttempt_SecondCycle_Returns30MinuteWait`
- `NextCycleAttempt_ThirdCycle_Returns2HourWait`
- `NextCycleAttempt_FourthCycle_Returns12HourWait`
- `NextCycleAttempt_FifthCycle_ReturnsFail`
- `NextCycleAttempt_HighCycleNumber_ReturnsFail` (e.g. 50)
- `NextCycleAttempt_ZeroOrNegative_TreatedAsFirstCycle` `[Theory]` over `0, -1, -100`
- `Default_ReturnsSingletonInstance` — `RetryPolicy.Default` returns the same reference across calls.
- `Decisions_AreValueTypes_Equality` — two `RetryDecision.Wait(TimeSpan.FromSeconds(1))` instances compare equal (struct record equality).

**Test data style per CLAUDE.md "Tests author their own test data inline":** Each test asserts the expected `TimeSpan` as an inline literal (`TimeSpan.FromSeconds(1)`, `TimeSpan.FromMinutes(5)`, etc.) rather than reading from a `RetryPolicy` constant or array. The dev plan §3.2 says "Tests reference them by index, not by hard-coded literals — the policy is the single source"; this conflicts with the CLAUDE.md convention. **Resolution:** the CLAUDE.md convention wins (tautological-test argument: a typo in the production array would also be a typo in the test). This is an explicit Open Question below — proceeding on the CLAUDE.md side unless overruled.

### `tests/FlashSkink.Tests/_TestSupport/FakeClockTests.cs`

- `UtcNow_Initially_ReturnsConstructorTime`
- `Advance_AdvancesUtcNow`
- `Delay_ZeroOrNegative_CompletesImmediately` `[Theory]` over `0, -1ms, -1h`
- `Delay_PositiveDelay_DoesNotCompleteWithoutAdvance`
- `Delay_AdvanceShortOfDeadline_DoesNotComplete`
- `Delay_AdvancePastDeadline_Completes`
- `Delay_AdvanceExactlyToDeadline_Completes` (boundary case: `<=` not `<`)
- `Delay_MultiplePending_AllPastDeadlineComplete_OthersStayPending`
- `Delay_PreCancelledToken_ReturnsFaulted`
- `Delay_TokenCancelledMidWait_TaskFaultsWithOCE`
- `Delay_CancelledTokenAfterAdvanceCompleted_NoEffect` — completion wins the race
- `PendingDelayCount_ReflectsActiveDelays`
- `Advance_DisposesRegistrations` — pre-/post-`Advance` `CancellationTokenSource.Token.Register` count is conserved (verified indirectly by registering a custom callback that should not be invoked after the matching delay completes)
- `Dispose_CancelsAllPending` — pending delays' tasks transition to `Cancelled` after `Dispose`
- `ConcurrentAdvanceAndDelay_NoDeadlock` — 50-task race scheduling delays while another task `Advance`s; assert no hang within a 5-second wall-clock budget (this is a real `Task.Delay`-bounded escape hatch, not a `FakeClock.Delay` — wall-clock-bounded by xUnit timeout if escapes that)

All tests use a fresh `FakeClock(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc))` instance per test; no shared state.

### Existing tests

No existing tests modified. `dotnet test` should be green for the entire pre-existing suite (Phase 0–2 + §3.1) before and after this PR.

## Acceptance criteria

- [ ] All listed files exist, build clean on `ubuntu-latest` and `windows-latest` with `--warnaserror`.
- [ ] `dotnet test` green: all Phase 0–2 + §3.1 tests still pass; all new §3.2 tests pass.
- [ ] `dotnet format --verify-no-changes` reports clean.
- [ ] `IClock` and `SystemClock` are public, documented, and live in `FlashSkink.Core.Abstractions.Time`.
- [ ] `RetryPolicy`, `RetryDecision`, `RetryOutcome` are public, documented, and live in `FlashSkink.Core.Upload`.
- [ ] `FakeClock` lives in `tests/FlashSkink.Tests/_TestSupport/` and is `internal sealed`.
- [ ] `Core.Abstractions` references no UI/Presentation/Core projects (assembly-layering test in CI passes).
- [ ] `ErrorCode.cs` is **not** modified (cross-cutting decision 2).
- [ ] No production code outside the listed files is modified.
- [ ] `RetryPolicy.Next*` XML docs include the literal phrase "Never throws".

## Line-of-code budget

| File | Approx LOC |
|---|---|
| `Core.Abstractions/Time/IClock.cs` | 35 |
| `Core.Abstractions/Time/SystemClock.cs` | 25 |
| `Core/Upload/RetryOutcome.cs` | 20 |
| `Core/Upload/RetryDecision.cs` | 45 |
| `Core/Upload/RetryPolicy.cs` | 110 |
| **src subtotal** | **~235** |
| `tests/_TestSupport/FakeClock.cs` | 140 |
| `tests/Upload/RetryPolicyTests.cs` | 220 |
| `tests/_TestSupport/FakeClockTests.cs` | 180 |
| **tests subtotal** | **~540** |
| **Total** | **~775** |

Small PR, dominated by test surface. The `FakeClock` test count is large because the fake-clock contract is load-bearing for the rest of Phase 3 — a bug here would silently warp every later timer test.

## Non-goals

- **No `RangeUploader`** — §3.3.
- **No `UploadQueueService`** — §3.4.
- **No `BrainMirrorService`** — §3.5.
- **No wiring of `IClock` into existing services.** `WritePipeline`, `FlashSkinkVolume`, etc. continue to use `DateTime.UtcNow` directly until/unless a later PR explicitly migrates them. (`PersistenceNotificationHandler` and friends are not in the upload-time path; they don't need `IClock` for V1.)
- **No health-status modulation** of the retry ladder — Phase 5 (§22.1). `RetryPolicy` is health-blind; the orchestrator decides whether to enter the loop, not how the ladder shapes itself.
- **No "jitter" added to delays.** §21.1 is deterministic backoff. Jitter is a post-V1 consideration if thundering-herd patterns appear.
- **No persistence** of cycle-attempt counters in this PR. The `TailUploads.AttemptCount` column already exists from Phase 1 and is incremented in §3.4; this PR is just the policy that consumes that integer.
- **No new `ErrorCode` values** — cross-cutting decision 2.
- **No `INetworkAvailabilityMonitor` integration** — that consumer lives in §3.4.
- **No singleton DI registration** of `SystemClock` — Phase 6's host bootstrap will wire it; tests construct directly.

## Open questions for Gate 1

### 1. Test-data style for retry delays — inline literals vs. read from `RetryPolicy`'s arrays

Dev plan §3.2 says: *"The §21.1 numbers — 1/4/16 s, 5/30/120/720 min, 5-cycle cap — are encoded as `static readonly TimeSpan[]` arrays inside `RetryPolicy`. Tests reference them by index, not by hard-coded literals — the policy is the single source."*

This conflicts with `CLAUDE.md` § "Conventions / Testing": *"Tests author their own test data inline. When production code has internal constants or lookup tables, tests do not reference them via `[InternalsVisibleTo]` — they construct equivalent values fresh. Reasons: keeps internals truly internal; avoids tautological tests where the production constant is the expected value (a typo passes); makes tests legible standalone."*

Per CLAUDE.md's stated reasoning, a typo from `TimeSpan.FromSeconds(4)` to `TimeSpan.FromSeconds(40)` in the production array would *not* be caught if tests read from the same array — both would agree. Inline literals (`Assert.Equal(TimeSpan.FromSeconds(4), decision.Delay)`) catch it.

**My recommendation:** follow CLAUDE.md (inline literals). The dev-plan instruction was written before this convention crystallised in CLAUDE.md and is the older guidance.

**If overruled:** the production arrays must be made `internal` (currently planned as `private static readonly`) and `[InternalsVisibleTo("FlashSkink.Tests")]` is already set on Core, so no further wiring needed. The change is purely a few lines in `RetryPolicy.cs` (private → internal) and the test bodies switching to `Assert.Equal(RetryPolicy.RangeDelays[1], decision.Delay)` style.

### 2. `IClock.Delay` return type — `ValueTask` vs. `Task<Result>`

Dev plan §3.2 spec says `ValueTask Delay(TimeSpan, CancellationToken)`. This deviates from Principle 1 (every public method returns `Result`/`Result<T>`). Documented as a Principle-14-style cancellation-as-exception case. Three options:

- **A. Keep `ValueTask`** (plan default) — matches `Task.Delay`'s shape; cancellation flows as `OperationCanceledException`; caller's catch-first ladder maps to `Result.Fail(Cancelled)`. Idiomatic with how every other awaited primitive is consumed.
- **B. Return `ValueTask<Result>`** — Principle-1-pure but introduces ceremony at every call site for the only failure mode (cancellation), which is already idiomatically expressed via `OperationCanceledException`.
- **C. Two methods** — `ValueTask DelayAsync(...)` and `bool TryDelay(...)` — overengineered for this use case.

**My recommendation:** A. The XML remarks on `IClock.Delay` explicitly cite Principle 1's reasoning and Principle 14's wrap-at-boundary pattern so the deviation is documented at the source.

### 3. `SystemClock.Instance` static singleton — keep or drop?

Convenience for callers that don't want to wire DI. Tests never use it (they construct `FakeClock` directly). `WritePipeline`-style services will inject `IClock` via constructor regardless. The singleton is defensible (`SystemClock` is genuinely stateless and the GC won't help anything) but adds a public surface that has to stay frozen.

**My recommendation:** keep it. Removing it later is non-breaking (it's never used by name in any signature). Adding it later if someone wants it is also non-breaking. Default to including; the cost is a single line.

---

*Plan ready for review. Stop at Gate 1 per `CLAUDE.md` step 3.*
