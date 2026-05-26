using System.Diagnostics.CodeAnalysis;
using FlashSkink.Core.Abstractions.Results;
using Xunit;

namespace FlashSkink.Tests;

/// <summary>
/// Test-only helpers that encode the "assert Success, then consume Value/Error"
/// invariant once, so individual tests don't scatter <c>!</c> across every
/// <c>.Value</c> / <c>.Error</c> access (principle 37). Each helper asserts the
/// expected Success state via <see cref="Assert.True(bool)"/> /
/// <see cref="Assert.False(bool)"/>, then returns the verified-non-null member.
/// </summary>
internal static class ResultAssertions
{
    /// <summary>
    /// Asserts the result succeeded and returns its <see cref="Result{T}.Value"/>.
    /// Fails the test with <see cref="Assert.True(bool)"/> if the result is a failure.
    /// </summary>
    [return: NotNull]
    public static T AssertValue<T>(this Result<T> r)
    {
        Assert.True(r.Success, FormatFailure(r.Error));
        // Value is non-null when Success is true (Result<T>'s [MemberNotNullWhen]
        // would prove it inside this method, but the analyzer can't see Assert.True's
        // [DoesNotReturnIf(false)] flow combined with the MemberNotNullWhen here — principle 37.
        return r.Value!;
    }

    /// <summary>
    /// Asserts the result failed and returns its <see cref="Result{T}.Error"/>.
    /// Fails the test with <see cref="Assert.False(bool)"/> if the result is a success.
    /// </summary>
    public static ErrorContext AssertError<T>(this Result<T> r)
    {
        Assert.False(r.Success, "Expected a failed result, got success.");
        // Error is non-null when Success is false; analyzer flow limitation as above (principle 37).
        return r.Error!;
    }

    /// <summary>
    /// Asserts the result failed and returns its <see cref="Result.Error"/>.
    /// Fails the test with <see cref="Assert.False(bool)"/> if the result is a success.
    /// </summary>
    public static ErrorContext AssertError(this Result r)
    {
        Assert.False(r.Success, "Expected a failed result, got success.");
        // Error is non-null when Success is false; analyzer flow limitation as above (principle 37).
        return r.Error!;
    }

    private static string FormatFailure(ErrorContext? error)
        => error is null
            ? "Expected a successful result, got failure with no error context."
            : $"Expected a successful result, got failure: {error.Code} — {error.Message}";
}
