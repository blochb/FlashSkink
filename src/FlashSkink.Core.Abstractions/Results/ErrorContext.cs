namespace FlashSkink.Core.Abstractions.Results;

/// <summary>
/// Carries all diagnostic information for a failed operation without retaining the raw
/// <see cref="Exception"/> object. The exception type name, message, and stack trace are
/// captured as strings; the exception itself is not stored.
/// </summary>
/// <remarks>
/// Metadata keys must never match *Token, *Key, *Password, *Secret, *Mnemonic, or *Phrase
/// (principle 26 — logging never contains secrets).
/// </remarks>
public sealed record ErrorContext
{
    /// <summary>The discriminated failure mode; callers switch on this to decide recovery strategy.</summary>
    public required ErrorCode Code { get; init; }

    /// <summary>A human-readable description of the failure, suitable for logging.</summary>
    public required string Message { get; init; }

    /// <summary>The full type name of the originating exception, or <see langword="null"/> if no exception was involved.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>The <see cref="Exception.Message"/> of the originating exception, or <see langword="null"/>.</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>The <see cref="Exception.StackTrace"/> of the originating exception, or <see langword="null"/>.</summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// Optional diagnostic key/value pairs. Never include secrets: tokens, keys, passwords,
    /// mnemonics, or phrases. Safe examples: blob IDs, provider IDs, HTTP status codes, SQLite error codes.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Creates an <see cref="ErrorContext"/> from a code, message, and optional exception.
    /// The exception's type name, message, and stack trace are captured as strings;
    /// the <see cref="Exception"/> object is not retained.
    /// </summary>
    /// <param name="code">The failure mode.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="exception">The originating exception, or <see langword="null"/>.</param>
    public static ErrorContext From(ErrorCode code, string message, Exception? exception)
        => new()
        {
            Code = code,
            Message = message,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = exception?.Message,
            StackTrace = exception?.StackTrace,
        };
}
