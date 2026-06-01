using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Canned <see cref="IProviderSetup"/> test double. Returns a caller-supplied
/// <see cref="IStorageProvider"/> from <see cref="CreateProviderAsync"/> and a caller-supplied
/// token envelope from <see cref="ExchangeCodeAsync"/>, so volume-level tests can exercise
/// <c>AddTailAsync</c> without a real cloud SDK. Records call arguments for assertions.
/// </summary>
internal sealed class FakeProviderSetup : IProviderSetup
{
    /// <inheritdoc/>
    public string ProviderType { get; init; } = "fake-cloud";

    /// <inheritdoc/>
    public string DisplayName { get; init; } = "Fake Cloud";

    /// <inheritdoc/>
    public ProviderSetupKind SetupKind { get; init; } = ProviderSetupKind.OAuth;

    /// <summary>Provider returned by <see cref="CreateProviderAsync"/>. When <see langword="null"/>, the call fails.</summary>
    public IStorageProvider? ProviderToReturn { get; set; }

    /// <summary>Token envelope returned by <see cref="ExchangeCodeAsync"/>.</summary>
    public byte[] ExchangeTokenBytes { get; set; } = [0x01, 0x02, 0x03, 0x04];

    /// <summary>When set, <see cref="ValidatePathAsync"/> returns this invalid result instead of valid.</summary>
    public string? ValidatePathInvalidReason { get; set; }

    /// <summary>Invoked inside <see cref="CreateProviderAsync"/> before returning, for failure-injection tests.</summary>
    public Func<Task>? BeforeCreateReturns { get; set; }

    // Recorded call arguments.
    public string? LastExchangeCode { get; private set; }
    public string? LastExchangeClientSecret { get; private set; }
    public string? LastCreateProviderId { get; private set; }
    public int CreateProviderCallCount { get; private set; }

    public Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri, string codeChallenge, ProviderCredentials credentials, CancellationToken ct)
        => Task.FromResult(Result<Uri>.Ok(new Uri("https://example.test/authorize?code_challenge=" + codeChallenge)));

    public Task<Result<byte[]>> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri,
        ProviderCredentials credentials, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        LastExchangeCode = code;
        LastExchangeClientSecret = credentials.ClientSecret;
        return Task.FromResult(Result<byte[]>.Ok(ExchangeTokenBytes));
    }

    public Task<Result<ValidationResult>> ValidatePathAsync(string path, string skinkRoot, CancellationToken ct)
        => Task.FromResult(Result<ValidationResult>.Ok(
            ValidatePathInvalidReason is null
                ? ValidationResult.Valid
                : ValidationResult.Invalid(ValidatePathInvalidReason)));

    public async Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId, string displayName, byte[] encryptedToken,
        ProviderCredentials credentials, string? providerConfigJson,
        ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        CreateProviderCallCount++;
        LastCreateProviderId = providerId;
        if (BeforeCreateReturns is not null)
        {
            await BeforeCreateReturns().ConfigureAwait(false);
        }
        return ProviderToReturn is null
            ? Result<IStorageProvider>.Fail(ErrorCode.Unknown, "FakeProviderSetup has no provider to return.")
            : Result<IStorageProvider>.Ok(ProviderToReturn);
    }
}
