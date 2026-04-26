namespace FlashSkink.Core.Abstractions.Results;

/// <summary>
/// Result type for operations that either succeed with no value or fail with an
/// <see cref="ErrorContext"/>. Every public Core method that can fail returns this type
/// or <see cref="Result{T}"/> — exceptions are never thrown across the Core public API
/// boundary (principle 1).
/// </summary>
public readonly record struct Result
{
    /// <summary><see langword="true"/> if the operation succeeded; <see langword="false"/> if it failed.</summary>
    public bool Success { get; }

    /// <summary>
    /// Diagnostic detail when <see cref="Success"/> is <see langword="false"/>;
    /// <see langword="null"/> on a successful result.
    /// </summary>
    public ErrorContext? Error { get; }

    private Result(bool success, ErrorContext? error)
    {
        Success = success;
        Error = error;
    }

    /// <summary>Creates a successful result.</summary>
    public static Result Ok() => new(true, null);

    /// <summary>
    /// Creates a failed result from an error code and message, with an optional originating exception.
    /// The exception's diagnostic information is captured as strings; the object is not retained.
    /// </summary>
    /// <param name="code">The failure mode.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="exception">The originating exception, or <see langword="null"/>.</param>
    public static Result Fail(ErrorCode code, string message, Exception? exception = null)
        => new(false, ErrorContext.From(code, message, exception));

    /// <summary>Creates a failed result from an existing <see cref="ErrorContext"/>, for propagation.</summary>
    /// <param name="error">The error context to propagate.</param>
    public static Result Fail(ErrorContext error) => new(false, error);
}
