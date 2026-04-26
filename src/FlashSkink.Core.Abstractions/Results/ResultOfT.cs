namespace FlashSkink.Core.Abstractions.Results;

/// <summary>
/// Result type for operations that either succeed with a value of type <typeparamref name="T"/>
/// or fail with an <see cref="ErrorContext"/>. Every public Core method that can fail and
/// produces a value returns this type — exceptions are never thrown across the Core public API
/// boundary (principle 1).
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly record struct Result<T>
{
    /// <summary><see langword="true"/> if the operation succeeded; <see langword="false"/> if it failed.</summary>
    public bool Success { get; }

    /// <summary>
    /// The success value when <see cref="Success"/> is <see langword="true"/>;
    /// <see langword="default"/> when <see cref="Success"/> is <see langword="false"/>.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Diagnostic detail when <see cref="Success"/> is <see langword="false"/>;
    /// <see langword="null"/> on a successful result.
    /// </summary>
    public ErrorContext? Error { get; }

    private Result(bool success, T? value, ErrorContext? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The success value.</param>
    public static Result<T> Ok(T value) => new(true, value, null);

    /// <summary>
    /// Creates a failed result from an error code and message, with an optional originating exception.
    /// <see cref="Value"/> is <see langword="default"/>. The exception's diagnostic information is
    /// captured as strings; the object is not retained.
    /// </summary>
    /// <param name="code">The failure mode.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="exception">The originating exception, or <see langword="null"/>.</param>
    public static Result<T> Fail(ErrorCode code, string message, Exception? exception = null)
        => new(false, default, ErrorContext.From(code, message, exception));

    /// <summary>Creates a failed result from an existing <see cref="ErrorContext"/>, for propagation.</summary>
    /// <param name="error">The error context to propagate.</param>
    public static Result<T> Fail(ErrorContext error) => new(false, default, error);
}
