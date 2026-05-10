using FlashSkink.Core.Abstractions.Time;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Test-only <see cref="IClock"/> that advances time deterministically.
/// Call <see cref="Advance"/> to move the clock forward and complete any pending delays
/// whose deadline has been reached. Thread-safe.
/// </summary>
internal sealed class FakeClock : IClock, IDisposable
{
    private readonly object _lock = new();
    private DateTime _now;
    private readonly List<PendingDelay> _pending = [];

    /// <param name="startTime">Initial value of <see cref="UtcNow"/>. Must have <see cref="DateTimeKind.Utc"/>.</param>
    public FakeClock(DateTime startTime)
    {
        _now = startTime;
    }

    /// <inheritdoc/>
    public DateTime UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }
    }

    /// <summary>Number of delay tasks currently awaiting completion. Useful for diagnostic assertions.</summary>
    public int PendingDelayCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask Delay(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        lock (_lock)
        {
            // Re-check after acquiring the lock — the clock may have advanced while we waited.
            DateTime deadline = _now + delay;
            if (_now >= deadline)
            {
                return ValueTask.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new PendingDelay(deadline, tcs);

            // Register cancellation outside the TCS set — if the token fires, complete via Cancel.
            CancellationTokenRegistration reg = ct.Register(static state =>
            {
                var (e, token) = ((PendingDelay, CancellationToken))state!;
                e.Tcs.TrySetCanceled(token);
            }, (entry, ct));

            entry.Registration = reg;

            // Insert sorted by deadline so Advance can sweep the front of the list.
            int insertAt = _pending.BinarySearch(entry, PendingDelay.DeadlineComparer.Instance);
            if (insertAt < 0)
            {
                insertAt = ~insertAt;
            }

            _pending.Insert(insertAt, entry);

            return new ValueTask(tcs.Task);
        }
    }

    /// <summary>
    /// Advances <see cref="UtcNow"/> by <paramref name="delta"/> and completes all pending delays
    /// whose deadline is at or before the new time.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        List<PendingDelay> toComplete;

        lock (_lock)
        {
            _now += delta;
            DateTime cutoff = _now;

            // Collect all entries whose deadline <= cutoff and remove them.
            int i = 0;
            while (i < _pending.Count && _pending[i].Deadline <= cutoff)
            {
                i++;
            }

            if (i == 0)
            {
                return;
            }

            toComplete = _pending.GetRange(0, i);
            _pending.RemoveRange(0, i);
        }

        // Complete outside the lock so continuations cannot re-enter while we hold it.
        foreach (PendingDelay entry in toComplete)
        {
            entry.Registration.Dispose();
            entry.Tcs.TrySetResult();
        }
    }

    /// <summary>Cancels all pending delays and releases resources.</summary>
    public void Dispose()
    {
        List<PendingDelay> toCancel;

        lock (_lock)
        {
            toCancel = [.. _pending];
            _pending.Clear();
        }

        foreach (PendingDelay entry in toCancel)
        {
            entry.Registration.Dispose();
            entry.Tcs.TrySetCanceled();
        }
    }

    private sealed class PendingDelay(DateTime deadline, TaskCompletionSource tcs)
    {
        public DateTime Deadline { get; } = deadline;
        public TaskCompletionSource Tcs { get; } = tcs;
        public CancellationTokenRegistration Registration { get; set; }

        public sealed class DeadlineComparer : IComparer<PendingDelay>
        {
            public static DeadlineComparer Instance { get; } = new();

            public int Compare(PendingDelay? x, PendingDelay? y) =>
                x!.Deadline.CompareTo(y!.Deadline);
        }
    }
}
