using Microsoft.Extensions.Logging;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// <see cref="ILogger{T}"/> implementation that captures log entries for assertion in tests.
/// Captures both the rendered message and (when available) the structured properties so tests
/// can match on named template parameters (<c>{FileId}</c>, <c>{ProviderId}</c>, ...) rather
/// than fragile substring matches. Supports an asynchronous, predicate-based wait so tests can
/// drive their progress off the production log signal instead of polling derived state.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = [];
    private readonly List<Waiter> _waiters = [];

    /// <summary>All captured log entries in order.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // FormattedLogValues (the type Microsoft.Extensions.Logging synthesises behind
        // structural templates) implements IReadOnlyList<KeyValuePair<string, object?>>. When
        // the caller used a structural template, we capture the parameters; otherwise the
        // property list is null and HasProperty returns false.
        IReadOnlyList<KeyValuePair<string, object?>>? properties =
            state as IReadOnlyList<KeyValuePair<string, object?>>;

        var entry = new LogEntry(logLevel, formatter(state, exception), exception, properties);

        List<TaskCompletionSource<LogEntry>>? toComplete = null;

        lock (_lock)
        {
            _entries.Add(entry);

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Predicate(entry))
                {
                    toComplete ??= [];
                    toComplete.Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        if (toComplete is not null)
        {
            foreach (var tcs in toComplete)
            {
                tcs.TrySetResult(entry);
            }
        }
    }

    /// <summary>Returns true if any entry at or above <paramref name="level"/> contains <paramref name="substring"/>.</summary>
    public bool HasEntry(LogLevel level, string substring)
    {
        lock (_lock)
        {
            return _entries.Any(e => e.Level >= level
                && e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Returns a task that completes when an entry matching <paramref name="predicate"/> has
    /// been observed. Existing entries are checked synchronously before subscribing, so a
    /// match that fired before the call returns immediately. If no match arrives within
    /// <paramref name="budget"/>, the returned task faults with <see cref="TimeoutException"/>.
    /// </summary>
    /// <remarks>
    /// The TCS is created with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
    /// per the standard Microsoft / Stephen-Cleary guidance — completing the TCS on the
    /// logging thread must never run a caller's await continuation inline, which would risk
    /// re-entering the production code under <c>_lock</c>.
    /// </remarks>
    public async Task<LogEntry> WaitForAsync(
        Predicate<LogEntry> predicate,
        TimeSpan budget,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<LogEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Waiter waiter;

        lock (_lock)
        {
            foreach (var existing in _entries)
            {
                if (predicate(existing))
                {
                    return existing;
                }
            }

            waiter = new Waiter(predicate, tcs);
            _waiters.Add(waiter);
        }

        try
        {
            return await tcs.Task.WaitAsync(budget, ct).ConfigureAwait(false);
        }
        finally
        {
            // Unregister on every exit path (success, timeout, cancellation) so a long-lived
            // logger cannot accumulate dead waiters across tests.
            lock (_lock)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    private readonly record struct Waiter(
        Predicate<LogEntry> Predicate,
        TaskCompletionSource<LogEntry> Tcs);
}

internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>>? Properties)
{
    /// <summary>
    /// Returns true if <see cref="Properties"/> contains a key/value pair matching
    /// <paramref name="name"/> and <paramref name="value"/> by <see cref="object.Equals(object?, object?)"/>.
    /// Returns false when no structured properties were captured.
    /// </summary>
    public bool HasProperty(string name, object? value)
    {
        if (Properties is null)
        {
            return false;
        }

        foreach (var kvp in Properties)
        {
            if (kvp.Key == name && Equals(kvp.Value, value))
            {
                return true;
            }
        }

        return false;
    }
}
