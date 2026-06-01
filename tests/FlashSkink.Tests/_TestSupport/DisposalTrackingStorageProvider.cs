using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Minimal <see cref="IStorageProvider"/> + <see cref="IAsyncDisposable"/> double that records
/// whether it was disposed. Used by <c>AddTailAsync</c> failure-disposal tests. All storage
/// operations fail benignly (the witness write on the success path is non-fatal); the tests only
/// assert on <see cref="Disposed"/>.
/// </summary>
internal sealed class DisposalTrackingStorageProvider : IStorageProvider, IAsyncDisposable
{
    public DisposalTrackingStorageProvider(string providerId, string providerType, string displayName)
    {
        ProviderID = providerId;
        ProviderType = providerType;
        DisplayName = displayName;
    }

    /// <summary>Set to <see langword="true"/> the first time <see cref="DisposeAsync"/> runs.</summary>
    public bool Disposed { get; private set; }

    public string ProviderID { get; }
    public string ProviderType { get; }
    public string DisplayName { get; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private static Result Fail() => Result.Fail(ErrorCode.Unknown, "DisposalTrackingStorageProvider is non-functional.");
    private static Result<T> Fail<T>() => Result<T>.Fail(ErrorCode.Unknown, "DisposalTrackingStorageProvider is non-functional.");

    public Task<Result<UploadSession>> BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct)
        => Task.FromResult(Fail<UploadSession>());

    public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct)
        => Task.FromResult(Fail<long>());

    public Task<Result> UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        => Task.FromResult(Fail());

    public Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct)
        => Task.FromResult(Fail<string>());

    public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct)
        => Task.FromResult(Result.Ok());

    public Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct)
        => Task.FromResult(Fail<Stream>());

    public Task<Result> DeleteAsync(string remoteId, CancellationToken ct)
        => Task.FromResult(Result.Ok());

    public Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct)
        => Task.FromResult(Result<bool>.Ok(false));

    public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
        => Task.FromResult(Result<IReadOnlyList<string>>.Ok([]));

    public Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(Fail<ProviderHealth>());

    public Task<Result<long>> GetUsedBytesAsync(CancellationToken ct)
        => Task.FromResult(Result<long>.Ok(0));

    public Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct)
        => Task.FromResult(Result<long?>.Ok(null));
}
