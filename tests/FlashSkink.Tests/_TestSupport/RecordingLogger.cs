using Microsoft.Extensions.Logging;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Simple <see cref="ILogger{T}"/> that captures log entries to a list for assertion in tests.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>All captured log entries in order.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>Returns true if any entry at or above <paramref name="level"/> contains <paramref name="substring"/>.</summary>
    public bool HasEntry(LogLevel level, string substring) =>
        _entries.Any(e => e.Level >= level && e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
