# PR 2.3 — Notification bus

**Branch:** pr/2.3-notification-bus
**Blueprint sections:** §4.2 (dependency graph), §8.1, §8.2, §8.3, §8.4, §8.5, §8.6; Principle 24; Principle 25; Principle 8 (Core → Presentation prohibition)
**Dev plan section:** phase-2 §2.3

## Scope

Delivers the in-process notification system that surfaces background-service failures to the user. Three layers, three projects:

1. **Contracts** in `FlashSkink.Core.Abstractions/Notifications/` — every Core publisher (`WritePipeline`, `ReadPipeline`, future upload/audit/healing services) and every Core handler (`PersistenceNotificationHandler`) must reference them, and per Principle 8 / §4.2 Core does not reference Presentation. The same logic that puts `Result`, `ErrorContext`, and `IStorageProvider` in `Abstractions` puts the notification contracts there too.
2. **Implementations** in `FlashSkink.Presentation/Notifications/` — `NotificationBus` (channel + dispatch loop) and `NotificationDispatcher` (fan-out + dedup + periodic flush). Consumer-facing infrastructure that may grow UI handlers in Phase 6.
3. **Persistence handler** in `FlashSkink.Core/Engine/PersistenceNotificationHandler.cs` — implements `INotificationHandler` (an `Abstractions` contract) and consumes `BackgroundFailureRepository` (Core); both reachable from Core directly, no layering violation.

Phase 2 registers exactly one handler (the persistence one) and exercises the system in isolation via tests. UI and CLI handlers land in Phase 6.

This PR also folds in the upstream documentation alignment (already merged into the working tree as part of the Gate-1 plan revision):

- `BLUEPRINT.md` §4.1 layout: `Notifications/` contracts moved under `Core.Abstractions/`, implementations under `Presentation/`, persistence handler under `Core/Engine/`.
- `BLUEPRINT.md` §4.2: explicit "Core does not reference Presentation" rule with rationale.
- `BLUEPRINT.md` §8.3: namespace updated to `FlashSkink.Core.Abstractions.Notifications` for contracts; bus-and-dispatcher split made explicit; `OccurredAt`/`DateTimeOffset` → `OccurredUtc`/`DateTime`.
- `BLUEPRINT.md` §8.5: persistence handler location pinned to `FlashSkink.Core/Engine/`.
- `CLAUDE.md` Principle 8: extended to encode "Core does not reference Presentation" with the contract/implementation split rationale.
- `dev-plan/phase-2-write-pipeline-and-phase-1-commit.md` §2.3 and goal bullet: rewritten to match.

The PR does **not** wire the bus into DI, the volume, or any pipeline — wiring lives in §2.5 (write pipeline), §2.6 (read pipeline), §2.7 (volume), and the host projects' `Program.cs`. This PR delivers types and exercises them in isolation via tests.

## Files to create

### Contracts — `src/FlashSkink.Core.Abstractions/Notifications/`
- `INotificationBus.cs` — public interface, `PublishAsync` only, ~12 lines
- `INotificationHandler.cs` — public interface, `HandleAsync` only, ~10 lines
- `Notification.cs` — sealed class, `required`-property record-style payload, ~40 lines
- `NotificationSeverity.cs` — public enum (`Info`, `Warning`, `Error`, `Critical`), ~12 lines

### Implementations — `src/FlashSkink.Presentation/Notifications/`
- `NotificationBus.cs` — `public sealed`, owns the `Channel<Notification>` and the dispatch loop; delegates per-notification work to `NotificationDispatcher`; tracks drop counter; `IAsyncDisposable`; ~140 lines
- `NotificationDispatcher.cs` — `public sealed`, owns handler list and dedup state; periodic flush of suppressed-count summaries; `IAsyncDisposable`; ~190 lines

### Handler — `src/FlashSkink.Core/Engine/`
- `PersistenceNotificationHandler.cs` — `public sealed`, implements `INotificationHandler`; writes `Error`/`Critical` to `BackgroundFailureRepository`; ~70 lines

### Tests — `tests/FlashSkink.Tests/Presentation/Notifications/`
- `NotificationBusTests.cs` — ~180 lines
- `NotificationDispatcherTests.cs` — ~250 lines

### Tests — `tests/FlashSkink.Tests/Engine/`
- `PersistenceNotificationHandlerTests.cs` — ~180 lines (Engine/, not Presentation/, because the SUT lives in Core/Engine)

## Files to modify

- `tests/FlashSkink.Tests/ArchitectureTests.cs` — add a new `[Fact] Core_DoesNotReference_Presentation` that asserts `FlashSkink.Core`'s referenced assemblies do not include `FlashSkink.Presentation`. This mechanises Principle 8's strengthened rule.

No NuGet changes; no `.csproj` edits. `FlashSkink.Core.Abstractions.csproj` already discovers files in new subfolders via implicit globbing. `FlashSkink.Presentation.csproj` already references `FlashSkink.Core` and `Core.Abstractions`. `FlashSkink.Tests.csproj` already references all three.

## Dependencies

- NuGet: none new (`System.Threading.Channels` is BCL)
- Project references: none new

## Documentation updates already applied (Gate-1 phase, in working tree)

These edits were made during the plan-revision step at user request and are part of this PR's diff. Listed here for completeness:

- `BLUEPRINT.md` §4.1: `Notifications/` contracts under `Core.Abstractions/`, implementations under `Presentation/`, `PersistenceNotificationHandler` under `Core/Engine/`.
- `BLUEPRINT.md` §4.2: new rule "Core does not reference Presentation" with contract/implementation rationale.
- `BLUEPRINT.md` §8.3: namespace, type split, `OccurredUtc`/`DateTime`, dispatcher decomposition.
- `BLUEPRINT.md` §8.5: persistence handler location.
- `CLAUDE.md` Principle 8: strengthened with Core→Presentation rule.
- `dev-plan/phase-2-write-pipeline-and-phase-1-commit.md`: §2.3 body and goal bullet rewritten.

No further docs are touched in implementation (Step 4). Any new doc drift discovered then is escalated, not silently fixed.

## Public API surface

### `FlashSkink.Core.Abstractions.Notifications.NotificationSeverity` (enum)
Summary intent: severity level for one notification; controls UI prominence and persistence policy.

```
Info     = 0,
Warning  = 1,
Error    = 2,
Critical = 3,
```

`Error` and `Critical` are persisted by `PersistenceNotificationHandler`; `Info` and `Warning` are not (§8.5).

### `FlashSkink.Core.Abstractions.Notifications.Notification` (sealed class)
Summary intent: one published notification — the unit of work flowing through bus and dispatcher.

- `string                Source              { get; init; }` (required)
- `NotificationSeverity  Severity            { get; init; }` (required)
- `string                Title               { get; init; }` (required) — user-facing; obeys Principle 25 (no appliance vocabulary)
- `string                Message             { get; init; }` (required) — user-facing; obeys Principle 25
- `ErrorContext?         Error               { get; init; }` — optional; carries the `ErrorCode` used as dedup key
- `DateTime              OccurredUtc         { get; init; } = DateTime.UtcNow;` — UTC; 1:1 with `BackgroundFailures.OccurredUtc`
- `bool                  RequiresUserAction  { get; init; }`

### `FlashSkink.Core.Abstractions.Notifications.INotificationBus` (interface)
Summary intent: publish surface; the only entry point services use.

- `ValueTask PublishAsync(Notification notification, CancellationToken ct = default)`

### `FlashSkink.Core.Abstractions.Notifications.INotificationHandler` (interface)
Summary intent: sink for dispatched notifications; implementations must not throw.

- `ValueTask HandleAsync(Notification notification, CancellationToken ct)`

### `FlashSkink.Presentation.Notifications.NotificationBus` (public sealed class : INotificationBus, IAsyncDisposable)
Summary intent: in-process bus owning a bounded channel and a single dispatch loop.

Constructor: `NotificationBus(NotificationDispatcher dispatcher, ILogger<NotificationBus> logger)`

- `ValueTask PublishAsync(Notification notification, CancellationToken ct = default)`
  Writes to the channel. If the channel is at capacity, `DropOldest` evicts the oldest queued notification silently — the bus tracks the eviction via an interlocked drop counter and emits a single `Warning` log line per drop with the cumulative count since the last successful drain. The channel is configured with `SingleReader = true`, `SingleWriter = false`.

- `ValueTask DisposeAsync()`
  Completes the channel writer, awaits the dispatch loop to drain remaining notifications, then disposes the dispatcher (which flushes any pending dedup summaries).

### `FlashSkink.Presentation.Notifications.NotificationDispatcher` (public sealed class : IAsyncDisposable)
Summary intent: per-notification fan-out plus `(Source, ErrorCode)` deduplication within a 60-second window.

Constructors:
- `NotificationDispatcher(ILogger<NotificationDispatcher> logger)` — production; defaults of 60 s window and 5 s flush interval.
- `NotificationDispatcher(TimeSpan dedupWindow, TimeSpan flushInterval, ILogger<NotificationDispatcher> logger)` — tests.

- `void RegisterHandler(INotificationHandler handler)` — append-only; not thread-safe with concurrent dispatch (handlers must be registered before the bus starts dispatching, i.e. at startup).

- `ValueTask DispatchAsync(Notification notification, CancellationToken ct)` — called by `NotificationBus`'s dispatch loop. Applies dedup (see contract below), then invokes every registered handler in order. Handler exceptions are caught, logged at `Warning` via `ILogger<NotificationDispatcher>`, and **never propagate** — a misbehaving handler must not interrupt fan-out (§8.3). `public` per blueprint §8.3's visibility rule — concrete infrastructure classes are public so host `Program.cs` can construct them and tests can drive them directly.

- `ValueTask DisposeAsync()` — stops the periodic flush timer, flushes any pending `(Source, ErrorCode)` entries with `SuppressedCount > 0` as a single summary notification each, awaits handler completion.

### `FlashSkink.Core.Engine.PersistenceNotificationHandler` (public sealed class : INotificationHandler)
Summary intent: persists `Error` and `Critical` notifications to `BackgroundFailures` so they survive process restart (Principle 24).

Constructor: `PersistenceNotificationHandler(BackgroundFailureRepository repository, ILogger<PersistenceNotificationHandler> logger)`

- `ValueTask HandleAsync(Notification notification, CancellationToken ct)`
  Behaviour:
  - `Severity is Info or Warning` → returns immediately (not persisted; §8.5).
  - `Severity is Error or Critical` → maps the notification to a `BackgroundFailure` record and calls `_repository.AppendAsync(failure, ct)`.
  - If `AppendAsync` returns a failed `Result`, logs the inner `ErrorContext` at `Warning` and returns. **Never publishes back to the bus** (would loop; §8.5 edge case).
  - Catches `OperationCanceledException` first → logs at `Information` and returns; cancellation is not a fault.
  - Catches `Exception` last → logs at `Error` and returns. Handler must never throw (the dispatch loop is defended in depth, but handlers should also defend themselves; §8.3).

## Internal types

### `NotificationDispatcher.DedupEntry` (private struct)
Tracks state per `(Source, ErrorCode)` key:

- `DateTime FirstOccurredUtc` — when the first dispatch happened
- `DateTime LastOccurredUtc` — most recent suppressed-or-dispatched occurrence
- `int SuppressedCount` — count of suppressions since the last dispatch within this window

Stored in `Dictionary<(string Source, string ErrorCode), DedupEntry>` guarded by a single `lock`. The dictionary is small (one entry per active failure key), so a coarse lock is acceptable; per-key contention is not a goal.

## Method-body contracts

### `NotificationBus.PublishAsync` — channel-full drop logging

```
1. Increment _inFlightCount via Interlocked.Increment.
2. If post-increment count > 100 (the channel capacity):
     a. Increment _dropsSinceLastDrain via Interlocked.Increment.
     b. Decrement _inFlightCount (compensate — DropOldest will evict, net stays at 100).
     c. Log Warning: "Notification channel at capacity; dropping oldest. Drops since last drain: {N}."
3. Await _channel.Writer.WriteAsync(notification, ct). With BoundedChannelFullMode.DropOldest,
   this completes immediately even at capacity (silently evicting the oldest entry).
```

The `_inFlightCount` counter is decremented in the dispatch loop after a notification is read from the channel; that decrement also clears `_dropsSinceLastDrain` to zero. This is best-effort drop reporting — the counter may briefly disagree with the channel's actual queue under contention, but the *log line* still records the drop event.

### `NotificationBus` — dispatch loop

```csharp
private async Task DispatchLoopAsync()
{
    await foreach (var notification in _channel.Reader.ReadAllAsync())
    {
        Interlocked.Decrement(ref _inFlightCount);
        Interlocked.Exchange(ref _dropsSinceLastDrain, 0);
        try { await _dispatcher.DispatchAsync(notification, CancellationToken.None); }
        catch (Exception ex)
        {
            // Defence in depth — dispatcher already swallows handler exceptions, but if
            // dispatcher itself throws (DI / lifecycle bug), we log and keep going.
            _logger.LogError(ex, "Dispatcher threw; loop continues.");
        }
    }
}
```

`CancellationToken.None` literal at the dispatch site is intentional: once a notification is read from the channel, we do not want to abandon dispatching it just because the bus is being disposed (Principle 17).

### `NotificationDispatcher.DispatchAsync` — dedup contract

```
1. If notification.Error is null:
     → bypass dedup; invoke handlers directly (jump to step 4).

2. key = (notification.Source, notification.Error.Code.ToString())
   now = DateTime.UtcNow
   under _dedupLock { ... }

3. If _dedup.TryGetValue(key, out var entry):
     elapsed = now - entry.FirstOccurredUtc
     if elapsed < _dedupWindow:
       entry.SuppressedCount += 1
       entry.LastOccurredUtc = now
       _dedup[key] = entry          // value-type write-back
       return                       // suppress; do not invoke handlers
     else:
       // Window has lapsed. Lazy flush the prior summary, then dispatch this notification fresh.
       if entry.SuppressedCount > 0:
         await EmitSummaryAsync(notification.Source, key.ErrorCode, entry, ct)
       _dedup[key] = new DedupEntry { FirstOccurredUtc = now, LastOccurredUtc = now, SuppressedCount = 0 }
   Else:
     _dedup[key] = new DedupEntry { FirstOccurredUtc = now, LastOccurredUtc = now, SuppressedCount = 0 }

4. foreach (var handler in _handlers):
     try { await handler.HandleAsync(notification, ct); }
     catch (Exception ex) { _logger.LogWarning(ex, "Handler {Type} failed; continuing.", handler.GetType().Name); }
```

`EmitSummaryAsync` constructs a `Notification` with:
- `Source = original Source`
- `Severity = Warning` (V1; per-occurrence original severities are not retained on the dedup entry)
- `Title = "Repeated background failure"`
- `Message = $"{key.ErrorCode}: {entry.SuppressedCount} additional occurrences within {_dedupWindow}."`
- `Error = null` (avoids re-deduplicating)

…and invokes handlers directly (does not re-publish to the bus, which would cause a loop).

### `NotificationDispatcher` — periodic flush

A `PeriodicTimer` runs on a background `Task` every `_flushInterval` (default 5 s). On each tick:

```
under _dedupLock:
  collect all keys whose (now - FirstOccurredUtc) >= _dedupWindow AND SuppressedCount > 0
  remove the keys (V1 chooses remove-after-flush; the next occurrence starts a fresh window)

Outside the lock: invoke handlers for the collected summaries.
```

The flush task is owned by the dispatcher and stopped in `DisposeAsync` (CTS cancel + `await _flushTask`).

### `PersistenceNotificationHandler.HandleAsync` — mapping

```
1. If notification.Severity is NotificationSeverity.Info or NotificationSeverity.Warning: return.

2. var failure = new BackgroundFailure {
     FailureId    = Guid.NewGuid().ToString(),
     OccurredUtc  = notification.OccurredUtc,
     Source       = notification.Source,
     ErrorCode    = notification.Error?.Code.ToString() ?? "Unknown",
     Message      = notification.Message,
     Metadata     = SerialiseMetadata(notification.Error?.Metadata),
     Acknowledged = false,
   };

3. try {
     var result = await _repository.AppendAsync(failure, ct);
     if (!result.Success) _logger.LogWarning(
       "Failed to persist background failure {FailureId}: {Code} {Message}",
       failure.FailureId, result.Error?.Code, result.Error?.Message);
   }
   catch (OperationCanceledException) { _logger.LogInformation("Persist cancelled."); }
   catch (Exception ex) { _logger.LogError(ex, "Unexpected error persisting background failure."); }
```

`SerialiseMetadata` is a private static helper using `System.Text.Json.JsonSerializer.Serialize` on the dictionary; returns `null` for `null` or empty input. Principle 26: the helper applies no key filtering — metadata sanitisation is the *publisher's* responsibility, enforced by the lint rule called out in CLAUDE.md Principle 26 (`*Token`, `*Key`, `*Password`, `*Secret`, `*Mnemonic`, `*Phrase` keys forbidden in `ErrorContext.Metadata`).

## Integration points

From prior PRs (consumed unchanged):

- `FlashSkink.Core.Abstractions.Results.ErrorContext` — `Code`, `Message`, `Metadata` properties (§1.1)
- `FlashSkink.Core.Abstractions.Results.ErrorCode` — string-formatted via `ToString()` for dedup keys and `BackgroundFailure.ErrorCode` (§1.1)
- `FlashSkink.Core.Abstractions.Models.BackgroundFailure` — `required`-property record (§1.6)
- `FlashSkink.Core.Metadata.BackgroundFailureRepository.AppendAsync(BackgroundFailure, CancellationToken)` returning `Task<Result>` (§1.6)
- `Microsoft.Extensions.Logging.ILogger<T>` (Abstractions only — Principle 28)

No prior PR exposes a notification consumer. Phase 2 §2.5 / §2.6 will be the first call sites.

## Principles touched

- **Principle 8** (Core does not reference Presentation) — verified by the new `Core_DoesNotReference_Presentation` architecture test, and structurally enforced by placing contracts in `Abstractions` rather than `Presentation`.
- **Principle 13** (`CancellationToken ct` last) — every public method that takes a CT has it last; defaulted to `CancellationToken.None` only on `INotificationBus.PublishAsync`.
- **Principle 14** (`OperationCanceledException` first catch) — `PersistenceNotificationHandler.HandleAsync` and any other site that catches.
- **Principle 15** (no bare `catch (Exception)` as the only catch) — handler exception isolation in dispatcher catches `Exception` after specific predecessors are not applicable; the dispatch-loop wrapper in `NotificationBus` is the standard "log and continue" loop guard, mirroring the §8.3 blueprint sample.
- **Principle 17** (`CancellationToken.None` literal in compensation paths) — `NotificationBus` dispatch-loop pass-through to dispatcher; `NotificationDispatcher.DisposeAsync` flush of pending summaries. `PersistenceNotificationHandler` does *not* use `CancellationToken.None` — it forwards the dispatcher's token, because persisting a single failure is interruptible.
- **Principle 24** (no background failure is silent) — this PR is *the implementation* of Principle 24's persistence pillar.
- **Principle 25** (appliance vocabulary discipline) — all string outputs from this PR (`Title`, `Message` in the summary path, log messages, doc comments) avoid "blob", "WAL", "stripe", "DEK", "PRAGMA".
- **Principle 26** (logging never contains secrets) — `Notification.Message` and `ErrorContext.Metadata` are passed through without inspection; the publisher is responsible for sanitisation.
- **Principle 27** (Core logs internally; callers log the `Result`) — `PersistenceNotificationHandler` logs its own outcome and the inner `ErrorContext` from `AppendAsync` once. No double-logging.
- **Principle 28** (Core / Presentation use only `Microsoft.Extensions.Logging.Abstractions`) — only abstractions referenced.

## Test spec

### `tests/FlashSkink.Tests/Presentation/Notifications/NotificationBusTests.cs`

**Class: `NotificationBusTests`** (`IAsyncLifetime`)

Uses a `RecordingDispatcher` test double — a sealed class deriving from `NotificationDispatcher` whose `DispatchAsync` (public per the API table) is overridden via the parameterised constructor and a no-op handler list, plus an internal `List<Notification>` capturing each call. Logger is a `RecordingLogger<T>` (~30-line hand-rolled `ILogger<T>` that captures log entries to a list — implemented inline in the test project under `tests/FlashSkink.Tests/_TestSupport/RecordingLogger.cs` if it doesn't already exist; verify and reuse).

- `PublishAsync_SingleNotification_ReachesDispatcher` — publish one; assert dispatcher received it within a 1-second timeout (`SpinWait` over the recording list).
- `PublishAsync_BurstAtCapacity_LogsDropWarning` — publish 200 notifications without giving the dispatcher time to drain (use a dispatcher whose `DispatchAsync` blocks on a `TaskCompletionSource` until the test releases it); assert the logger captured at least one `Warning` containing "channel at capacity".
- `PublishAsync_AfterDispose_Throws` — dispose the bus; calling `PublishAsync` returns a `ValueTask` that throws `ChannelClosedException`. Assert via `Assert.ThrowsAsync<ChannelClosedException>`.
- `DisposeAsync_DrainsPendingNotifications` — publish 5 notifications; immediately dispose; assert dispatcher saw all 5.
- `Dispatcher_ThrowingException_DoesNotKillLoop` — register a dispatcher that throws on the 2nd notification; publish 5; assert 4 reached subsequent recording (the 2nd is lost; the rest are not). Asserts the loop's defence-in-depth `try/catch`.

### `tests/FlashSkink.Tests/Presentation/Notifications/NotificationDispatcherTests.cs`

**Class: `NotificationDispatcherTests`** (`IAsyncLifetime`)

Uses a `RecordingHandler` test double implementing `INotificationHandler`; appends every received notification to an internal list. Some tests use `Mock<INotificationHandler>` from Moq for verification semantics.

- `DispatchAsync_NoError_BypassesDedup` — publish two notifications with `Error = null`, same `Source`, identical `Title`. Assert both reach the handler.
- `DispatchAsync_FirstWithError_Dispatches` — single notification with non-null `Error`. Assert handler received it.
- `DispatchAsync_DuplicateInWindow_Suppresses` — dispatch two notifications with same `(Source, Error.Code)` within the dedup window (set window to 1 s in test). Assert handler received exactly the first.
- `DispatchAsync_DuplicateAcrossWindow_DispatchesBoth` — dispatch two notifications, same key, separated by `dedupWindow + small`. Assert handler received both.
- `DispatchAsync_DuplicateAcrossWindow_EmitsSummaryWhenSuppressedCountAboveOne` — dispatch first, then 3 suppressed copies, then wait past window, then publish another with same key. Assert handler received: first, summary ("3 additional occurrences"), the new one.
- `PeriodicFlush_EmitsSummaryAfterWindow` — set window=200 ms, flushInterval=50 ms; dispatch one then 2 suppressed within 100 ms; wait 400 ms; assert handler received first then summary, without any further user dispatch triggering it.
- `RegisterHandler_TwoHandlers_BothReceive` — dispatch one; both handlers see it.
- `Handler_ThrowingException_OtherHandlersStillReceive` — register handlers H1 (throws), H2 (records). Dispatch one. Assert H2 received it; assert log captured `Warning` for H1.
- `DisposeAsync_FlushesPendingSummaries` — dispatch first then 2 suppressed; immediately dispose. Assert handler received first then a summary.
- `DispatchAsync_NullError_DifferentSources_BothDispatch` — sanity check that null-Error path doesn't accidentally collapse on Source alone.

### `tests/FlashSkink.Tests/Engine/PersistenceNotificationHandlerTests.cs`

**Class: `PersistenceNotificationHandlerTests`** (`IAsyncLifetime`)

Uses a real `BackgroundFailureRepository` against an in-memory SQLite from `BrainTestHelper` (already exists per §1.6). Preferred over mocking because it exercises actual SQL and column types, catching mapping bugs.

- `HandleAsync_InfoNotification_DoesNotPersist` — publish `Info` notification; assert `ListUnacknowledgedAsync` returns empty.
- `HandleAsync_WarningNotification_DoesNotPersist` — publish `Warning`; assert empty.
- `HandleAsync_ErrorNotification_PersistsRow` — publish `Error` with full `ErrorContext`; assert row exists with correct `Source`, `ErrorCode`, `Message`, `OccurredUtc`, and serialised `Metadata`.
- `HandleAsync_CriticalNotification_PersistsRow` — same as Error, with `Critical` severity.
- `HandleAsync_NullError_PersistsErrorCodeUnknown` — `Error` notification with `Error = null`; assert row's `ErrorCode = "Unknown"`.
- `HandleAsync_RepositoryFailure_LogsAndSwallows` — pass a repository pointed at a *closed* SQLite connection so `AppendAsync` returns `Result.Fail`. Assert `HandleAsync` does not throw; assert log captured `Warning` mentioning the failure ID.
- `HandleAsync_Cancellation_LogsAtInformation_DoesNotThrow` — pre-cancelled CT; assert `HandleAsync` returns without throwing; log captured `Information` line.
- `HandleAsync_MetadataRoundTrips` — publish notification with `ErrorContext.Metadata = {("BlobId", "x"), ("ProviderId", "FileSystem")}`; assert persisted `Metadata` JSON deserialises back to the same dictionary.

### `tests/FlashSkink.Tests/ArchitectureTests.cs` — added test

```csharp
[Fact]
public void Core_DoesNotReference_Presentation()
{
    var refs = GetAssembly("FlashSkink.Core").GetReferencedAssemblies()
        .Select(a => a.Name ?? string.Empty);
    Assert.DoesNotContain(refs, r =>
        r.Equals("FlashSkink.Presentation", StringComparison.OrdinalIgnoreCase));
}
```

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (`ubuntu-latest`, `windows-latest`)
- [ ] All new tests pass; existing tests still pass
- [ ] `dotnet format --verify-no-changes` clean
- [ ] Contracts (`INotificationBus`, `INotificationHandler`, `Notification`, `NotificationSeverity`) live in `src/FlashSkink.Core.Abstractions/Notifications/`
- [ ] Implementations (`NotificationBus`, `NotificationDispatcher`) live in `src/FlashSkink.Presentation/Notifications/`
- [ ] `PersistenceNotificationHandler` lives in `src/FlashSkink.Core/Engine/`
- [ ] `Core_DoesNotReference_Presentation` architecture test passes
- [ ] No `Core → Presentation` reference is introduced; existing `ArchitectureTests` still pass
- [ ] `NotificationBus` and `NotificationDispatcher` are `public sealed` and `IAsyncDisposable`
- [ ] `NotificationDispatcher.DispatchAsync` is `public`
- [ ] `Channel<Notification>` is bounded at capacity 100 with `BoundedChannelFullMode.DropOldest`, `SingleReader = true`, `SingleWriter = false`
- [ ] Channel-full drop logs at `Warning` via `ILogger<NotificationBus>` and includes drops-since-last-drain count
- [ ] Dedup applies only when `notification.Error is not null`; key is `(Source, Error.Code.ToString())`; window default 60 s; flush interval default 5 s
- [ ] `Info` and `Warning` are not persisted; `Error` and `Critical` are persisted
- [ ] Repository failures inside `PersistenceNotificationHandler` are logged and swallowed (no re-publish; no exception)
- [ ] All notification `Title` / `Message` strings in tests and code obey Principle 25
- [ ] All async methods take `CancellationToken ct` last (Principle 13)
- [ ] `OperationCanceledException` is the first catch in `PersistenceNotificationHandler.HandleAsync` (Principle 14)
- [ ] No new `ErrorCode` values added (cross-cutting decision 4)
- [ ] BLUEPRINT.md, CLAUDE.md, dev-plan changes (already in working tree from Gate-1) are committed in this PR

## Line-of-code budget

### Non-test
- `INotificationBus.cs` — ~12 lines
- `INotificationHandler.cs` — ~10 lines
- `NotificationSeverity.cs` — ~12 lines
- `Notification.cs` — ~40 lines
- `NotificationBus.cs` — ~140 lines
- `NotificationDispatcher.cs` — ~190 lines
- `PersistenceNotificationHandler.cs` — ~70 lines
- **Total non-test: ~474 lines**

### Test
- `NotificationBusTests.cs` — ~180 lines
- `NotificationDispatcherTests.cs` — ~250 lines
- `PersistenceNotificationHandlerTests.cs` — ~180 lines
- `ArchitectureTests.cs` modification — ~+10 lines
- (Possibly `_TestSupport/RecordingLogger.cs` — ~30 lines if not already present)
- **Total test: ~620–650 lines**

### Docs (already in working tree from Gate-1)
- `BLUEPRINT.md` — ~+25 / -5 lines
- `CLAUDE.md` — ~+1 / -1 line
- `dev-plan/phase-2-write-pipeline-and-phase-1-commit.md` — ~+15 / -10 lines
- (No further doc edits in implementation step.)

## Non-goals

- Do NOT register the bus in DI in any host project — Phase 6.
- Do NOT add UI or CLI handlers — Phase 6.
- Do NOT wire the bus into `FlashSkinkVolume`, `WritePipeline`, or `ReadPipeline` — §2.5, §2.6, §2.7.
- Do NOT add a "background-failures-on-startup" surface — Phase 6.
- Do NOT add throttling beyond the 60-second `(Source, ErrorCode)` dedup — V1.
- Do NOT add `IObservable<Notification>` reactive surface — explicitly rejected by §8.2.
- Do NOT support multi-bus / multi-volume — one bus per process.
- Do NOT persist `Notification.RequiresUserAction` — it is consumed by Phase 6 UI handlers, not by `BackgroundFailures`.
- Do NOT add a notification-replay-from-`BackgroundFailures` mechanism here — that is a Phase 6 startup concern.
- Do NOT touch `BackgroundFailureRepository` — consumed unchanged from §1.6.
- Do NOT make further changes to `BLUEPRINT.md`, `CLAUDE.md`, or `dev-plan/` during implementation — the Gate-1 doc edits are final unless Gate 2 surfaces a problem and the user approves a re-revision.
