namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Outcome of a non-throwing validation call (currently only
/// <see cref="IProviderSetup.ValidatePathAsync"/>). Distinguishes "the operation succeeded and the
/// answer is invalid" from "the operation failed with an error" — the latter is carried by the
/// enclosing <see cref="Results.Result{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Invariant: <see cref="IsValid"/> ⇒ <see cref="Reason"/> is <see langword="null"/>; and
/// <see cref="IsValid"/> is <see langword="false"/> ⇒ <see cref="Reason"/> is non-null. The
/// constructor is <c>private</c>; the static factories <see cref="Valid"/> and
/// <see cref="Invalid(string)"/> are the only construction paths and enforce the invariant
/// structurally.
/// </para>
/// </remarks>
public sealed record ValidationResult
{
    /// <summary><see langword="true"/> ⇒ the validated input was acceptable.</summary>
    public bool IsValid { get; }

    /// <summary>Reason the validation failed; <see langword="null"/> when <see cref="IsValid"/> is <see langword="true"/>.</summary>
    public string? Reason { get; }

    private ValidationResult(bool isValid, string? reason)
    {
        IsValid = isValid;
        Reason = reason;
    }

    /// <summary>Singleton "validation passed" result. Allocation-free.</summary>
    public static ValidationResult Valid { get; } = new(true, null);

    /// <summary>Constructs a "validation failed" result with the supplied user-facing reason.</summary>
    public static ValidationResult Invalid(string reason) => new(false, reason);
}
