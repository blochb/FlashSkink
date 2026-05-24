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
/// <see cref="IsValid"/> is <see langword="false"/> ⇒ <see cref="Reason"/> is non-null.
/// The static factories <see cref="Valid"/> and <see cref="Invalid(string)"/> are the only
/// supported construction paths.
/// </para>
/// </remarks>
public sealed record ValidationResult(bool IsValid, string? Reason)
{
    /// <summary>Singleton "validation passed" result. Allocation-free.</summary>
    public static ValidationResult Valid { get; } = new(true, null);

    /// <summary>Constructs a "validation failed" result with the supplied user-facing reason.</summary>
    public static ValidationResult Invalid(string reason) => new(false, reason);
}
