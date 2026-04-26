using FlashSkink.Core.Abstractions.Results;
using Xunit;

namespace FlashSkink.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Result_Ok_HasSuccessTrue()
    {
        var result = Result.Ok();

        Assert.True(result.Success);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Result_Fail_WithCodeAndMessage_HasSuccessFalse()
    {
        var result = Result.Fail(ErrorCode.Unknown, "msg");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
        Assert.Equal("msg", result.Error.Message);
        Assert.Null(result.Error.ExceptionType);
    }

    [Fact]
    public void Result_Fail_WithException_CapturesExceptionStrings()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { captured = ex; }

        var result = Result.Fail(ErrorCode.Unknown, "msg", captured);

        Assert.Equal("System.InvalidOperationException", result.Error!.ExceptionType);
        Assert.Equal("boom", result.Error.ExceptionMessage);
        Assert.NotNull(result.Error.StackTrace);
    }

    [Fact]
    public void Result_Fail_WithException_DoesNotRetainExceptionReference()
    {
        var contextType = typeof(ErrorContext);
        var exceptionProperties = contextType.GetProperties()
            .Where(p => typeof(Exception).IsAssignableFrom(p.PropertyType))
            .ToList();

        Assert.Empty(exceptionProperties);
    }

    [Fact]
    public void Result_Fail_WithErrorContext_PropagatesContext()
    {
        var context = ErrorContext.From(ErrorCode.Cancelled, "cancelled", null);

        var result = Result.Fail(context);

        Assert.False(result.Success);
        Assert.Same(context, result.Error);
    }
}

public class ResultOfTTests
{
    [Fact]
    public void ResultOfT_Ok_HasSuccessTrueAndValue()
    {
        var result = Result<int>.Ok(42);

        Assert.True(result.Success);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ResultOfT_Ok_WithReferenceType_HasValue()
    {
        var result = Result<string>.Ok("hello");

        Assert.True(result.Success);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void ResultOfT_Fail_HasSuccessFalseAndDefaultValue()
    {
        var result = Result<int>.Fail(ErrorCode.Unknown, "msg");

        Assert.False(result.Success);
        Assert.Equal(default, result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ResultOfT_Fail_WithException_CapturesExceptionStrings()
    {
        Exception captured;
        try { throw new ArgumentException("bad arg"); }
        catch (Exception ex) { captured = ex; }

        var result = Result<string>.Fail(ErrorCode.Unknown, "msg", captured);

        Assert.Equal("System.ArgumentException", result.Error!.ExceptionType);
        Assert.Equal("bad arg", result.Error.ExceptionMessage);
        Assert.NotNull(result.Error.StackTrace);
    }

    [Fact]
    public void ResultOfT_Fail_WithErrorContext_PropagatesContext()
    {
        var context = ErrorContext.From(ErrorCode.DatabaseCorrupt, "corrupt", null);

        var result = Result<string>.Fail(context);

        Assert.False(result.Success);
        Assert.Same(context, result.Error);
    }

    [Fact]
    public void ResultOfT_Ok_NullableReferenceType_Allowed()
    {
        var result = Result<string?>.Ok(null);

        Assert.True(result.Success);
        Assert.Null(result.Value);
        Assert.Null(result.Error);
    }
}

public class ErrorContextTests
{
    [Fact]
    public void ErrorContext_From_NullException_ProducesNullFields()
    {
        var ctx = ErrorContext.From(ErrorCode.Cancelled, "msg", null);

        Assert.Equal(ErrorCode.Cancelled, ctx.Code);
        Assert.Equal("msg", ctx.Message);
        Assert.Null(ctx.ExceptionType);
        Assert.Null(ctx.ExceptionMessage);
        Assert.Null(ctx.StackTrace);
    }

    [Fact]
    public void ErrorContext_From_RealException_CapturesTypeNameAndMessage()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { captured = ex; }

        var ctx = ErrorContext.From(ErrorCode.Unknown, "msg", captured);

        Assert.Equal("System.InvalidOperationException", ctx.ExceptionType);
        Assert.Equal("boom", ctx.ExceptionMessage);
    }

    [Fact]
    public void ErrorContext_From_RealException_StackTraceNonNullAfterThrow()
    {
        Exception captured;
        try { throw new Exception("x"); }
        catch (Exception ex) { captured = ex; }

        var ctx = ErrorContext.From(ErrorCode.Unknown, "msg", captured);

        Assert.NotNull(ctx.StackTrace);
    }

    [Fact]
    public void ErrorContext_From_RealException_StackTraceNullBeforeThrow()
    {
        var ex = new Exception("x");

        var ctx = ErrorContext.From(ErrorCode.Unknown, "msg", ex);

        Assert.Null(ctx.StackTrace);
    }

    [Fact]
    public void ErrorContext_WithMetadata_CanBeAdded()
    {
        var ctx = ErrorContext.From(ErrorCode.Unknown, "msg", null)
            with { Metadata = new Dictionary<string, string> { ["k"] = "v" } };

        Assert.Equal("v", ctx.Metadata!["k"]);
    }

    [Fact]
    public void ErrorContext_Metadata_IsNullByDefault_FromFactory()
    {
        var ctx = ErrorContext.From(ErrorCode.Unknown, "msg", null);

        Assert.Null(ctx.Metadata);
    }
}

public class ErrorCodeTests
{
    [Fact]
    public void ErrorCode_Unknown_IsZero()
    {
        Assert.Equal(0, (int)ErrorCode.Unknown);
    }

    [Fact]
    public void ErrorCode_AllPhase1Values_AreDefined()
    {
        // Blueprint §6.4 names are authoritative; dev plan §1.1 approximations are noted in the plan.
        var phase1Values = new[]
        {
            "Cancelled",
            "Unknown",
            "DatabaseCorrupt",
            "DatabaseLocked",
            "DatabaseWriteFailed",
            "VolumeCorrupt",          // vault corruption maps here per blueprint §6.4
            "VolumeNotFound",         // vault-not-found maps here per blueprint §6.4
            "VolumeIncompatibleVersion",
            "InvalidPassword",
            "InvalidMnemonic",
            "EncryptionFailed",       // CryptoFailed maps here per blueprint §6.4
            "DecryptionFailed",       // CryptoFailed maps here per blueprint §6.4
            "ChecksumMismatch",
            "UsbFull",
            "StagingFailed",
            "PathConflict",
            "CyclicMoveDetected",
            "ConfirmationRequired",
            "SingleInstanceLockHeld",
        };

        foreach (var name in phase1Values)
        {
            Assert.True(Enum.IsDefined(typeof(ErrorCode), name), $"ErrorCode.{name} is not defined");
        }
    }

    [Fact]
    public void ErrorCode_HasNoNegativeValues()
    {
        var values = Enum.GetValues<ErrorCode>();

        Assert.All(values, v => Assert.True((int)v >= 0, $"ErrorCode.{v} has negative value {(int)v}"));
    }
}
