# fix — Drop `CancellationToken` from compensation methods in `UploadQueueService`

**Branch:** fix/compensation-ct-drop  
**Blueprint sections:** §6.7 (compensation paths), §15 (upload lifecycle)  
**Related spike:** spike/upload-commit-dispose-race

## Scope

Two private methods in `UploadQueueService` — `ApplyCompletedAsync` and
`ApplyPermanentAsync` — carry a `CancellationToken ct` parameter that they must
not honor after the upload side-effect is complete. Both currently call
`ct.ThrowIfCancellationRequested()` at the top of the method body, then use
`CancellationToken.None` for every internal await. The guard is the bug: once
these methods are entered, the brain commit is compensation and must run to
completion (Principle 17). Cancelling before the commit leaves either a blob
on the tail without a matching `TailUploads = UPLOADED` row, or a terminal row
stranded at `UPLOADING` for recovery.

The structural fix is to remove `ct` from both signatures. The type system then
prevents the bug class: the guard can't be written, a future maintainer can't
accidentally pass `ct` to an internal await, and the asymmetric dispatch at
`ApplyOutcomeAsync` makes the two cancellation contracts visually distinct.

Reference: `PromoteToPermanentAsync` (the cycles-exhausted promotion path) was
already written correctly with no `ct` parameter and serves as the in-codebase
model.

Reproduction confirmed: stress loop in
`tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs`
(`FiveWrites_AllUploaded_StressLoop`) produced `uploaded=4 sessions=1` at
~2–4% frequency per 50-iteration run on Windows.

## Files to modify

- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — two signature changes,
  two guard deletions, two dispatch changes, comment trim (~−25 lines)
- `CLAUDE.md` — one sentence added to Principle 17 (~+2 lines)
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs` —
  update `Skip` message on the stress reproducer to reflect fix status (~−1/+1 line)

## Public API surface

None changed. Both methods are `private`.

## Internal types

No new types. Changes are signature + call-site only.

## Method-body contracts

### `ApplyCompletedAsync` (after)

```
Signature:  private async Task<Result> ApplyCompletedAsync(
                TailUploadRow row, VolumeFile file, IStorageProvider provider,
                UploadOutcome outcome)
```

- **No `CancellationToken` parameter.** All internal awaits use
  `CancellationToken.None` as a literal (Principle 17).
- Precondition: the blob is already on the tail; `RangeUploader.UploadAsync`
  returned `Completed`. This method must run to completion regardless of
  concurrent `DisposeAsync`.
- Postcondition on success: `TailUploads.Status = UPLOADED`,
  `UploadSessions` row deleted, activity-log entry appended.
- On `txFailure`: delegates to `OnTerminalBrainFailureAsync`
  (no `ct` either — that method already takes none).

### `ApplyPermanentAsync` (after)

```
Signature:  private async Task<Result> ApplyPermanentAsync(
                TailUploadRow row, VolumeFile file, IStorageProvider provider,
                UploadOutcome outcome)
```

- **No `CancellationToken` parameter.** Same rationale as above.
- Precondition: the upload returned `PermanentFailure`. `ProcessOneAsync` has
  already called `MarkUploadingAsync`; the row is at `UPLOADING`.
- Postcondition on success: `TailUploads.Status = TERMINAL_FAILED` (cycle cap
  reached), `UploadSessions` row deleted, failure notification published,
  activity-log entry appended.
- On `txFailure`: delegates to `OnTerminalBrainFailureAsync`.

### `ApplyOutcomeAsync` (updated dispatch)

```
UploadOutcomeStatus.Completed        => ApplyCompletedAsync(row, file, provider, outcome),
UploadOutcomeStatus.RetryableFailure => ApplyRetryableAsync(row, file, provider, outcome, ct),
UploadOutcomeStatus.PermanentFailure => ApplyPermanentAsync(row, file, provider, outcome),
```

The visual asymmetry — two calls without `ct`, one with — is the documentation.
`ApplyRetryableAsync` correctly continues to accept `ct` since retry decisions
(backing off, sleeping, cycling) are legitimately cancellable.

## Changes in detail

### `UploadQueueService.cs`

1. **`ApplyCompletedAsync`**
   - Remove `CancellationToken ct` from parameter list (line 620).
   - Delete `ct.ThrowIfCancellationRequested();` (line 635).
   - Replace the multi-paragraph Principle-17 comment block (lines 622–634)
     with two lines: one for the no-cancel rationale, one for the gate-scope
     boundary.

2. **`ApplyPermanentAsync`**
   - Remove `CancellationToken ct` from parameter list (line 750).
   - Delete `ct.ThrowIfCancellationRequested();` (line 772).
   - Replace the multi-paragraph Principle-17 comment block (lines 756–771)
     with the same two-line pattern.

3. **`ApplyOutcomeAsync`** (lines 605–616)
   - Remove `ct` argument from the `Completed` and `PermanentFailure` dispatch
     arms.

### `CLAUDE.md` — Principle 17

Append after the last sentence of Principle 17:

> Private compensation methods (those that must complete after an irreversible
> side effect) correctly omit the `CancellationToken` parameter — this makes
> the non-cancellability structurally enforced rather than comment-enforced.
> `PromoteToPermanentAsync` and the post-fix `ApplyCompletedAsync` /
> `ApplyPermanentAsync` are the canonical examples.

### Test file

Update the `Skip` reason on `FiveWrites_AllUploaded_StressLoop` to record that
the fix eliminates the flake (stress loop confirmed consistently passing after
the change).

## Principles touched

- Principle 17 (compensation paths must not be cancelled mid-flight)
- Principle 13 (ct last — private compensation methods are the sanctioned
  exception)

## Test spec

No new tests required. The correct behavior is already covered by the existing
`FiveWrites_AllUploaded_SessionsEmpty` test (which was the original flaky test).
The skipped `FiveWrites_AllUploaded_StressLoop` serves as a regression harness:
un-skip locally to confirm the fix holds across 50 iterations.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets
- [ ] All existing tests pass (including `FiveWrites_AllUploaded_SessionsEmpty`)
- [ ] `FiveWrites_AllUploaded_StressLoop` passes consistently when un-skipped
      locally (manual gate — not run in CI due to 16 s runtime)
- [ ] No non-test code changes beyond the two signature edits, two guard
      deletions, and three dispatch-line updates

## Line-of-code budget

- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — net ~−25 lines
- `CLAUDE.md` — net ~+3 lines
- `tests/…/FlashSkinkVolumeUploadIntegrationTests.cs` — net ~0 lines (comment change)

## Non-goals

- Do NOT change `ApplyRetryableAsync` — it correctly honors `ct`.
- Do NOT change `PromoteToPermanentAsync` — it is already the reference
  implementation.
- Do NOT sweep other files outside `UploadQueueService.cs` — all other
  compensation sites audited and already correct.
- Do NOT add a new integration test — existing coverage is sufficient.
- Do NOT change the `FiveWrites_AllUploaded_StressLoop` Skip default — it is
  intentionally excluded from CI due to runtime; it exists as a manual
  regression harness.
