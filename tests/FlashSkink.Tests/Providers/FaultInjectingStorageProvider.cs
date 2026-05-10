using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Tests.Providers;

/// <summary>
/// Decorator over any <see cref="IStorageProvider"/> that adds deterministic fault injection
/// for testing retry, escalation, and session-expiry scenarios. Dev plan §3.1.
/// </summary>
/// <remarks>
/// Latency injection uses <see cref="Task.Delay(TimeSpan)"/> directly in §3.1. §3.3 will swap
/// this to <c>IClock.Delay</c> once the clock abstraction (§3.2) is available.
/// </remarks>
internal sealed class FaultInjectingStorageProvider : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private int _failNextRangeCount;
    private ErrorCode _failNextRangeCode = ErrorCode.UploadFailed;
    private int _expiryAfterRanges = int.MaxValue;
    private int _rangesUploaded;
    private TimeSpan _rangeLatency = TimeSpan.Zero;

    public FaultInjectingStorageProvider(IStorageProvider inner)
    {
        _inner = inner;
    }

    public string ProviderID => _inner.ProviderID;
    public string ProviderType => _inner.ProviderType;
    public string DisplayName => _inner.DisplayName;

    // ── Fault knobs ───────────────────────────────────────────────────────────────────────────

    /// <summary>Causes the next <see cref="UploadRangeAsync"/> call to fail with <see cref="ErrorCode.UploadFailed"/>.</summary>
    public void FailNextRange() => FailNextRangeWith(ErrorCode.UploadFailed);

    /// <summary>Causes the next <see cref="UploadRangeAsync"/> call to fail with <paramref name="code"/>.</summary>
    /// <remarks>
    /// When stacked with prior calls, all pending failures share the last-set code — stacking
    /// different error codes per failure is not supported by this implementation.
    /// </remarks>
    public void FailNextRangeWith(ErrorCode code)
    {
        _failNextRangeCount++;
        _failNextRangeCode = code;
    }

    /// <summary>
    /// Causes <see cref="UploadRangeAsync"/> to return <see cref="ErrorCode.UploadSessionExpired"/>
    /// on the call at index <paramref name="rangesUploaded"/> (0-based count of successful uploads).
    /// </summary>
    public void ForceSessionExpiryAfter(int rangesUploaded) =>
        _expiryAfterRanges = rangesUploaded;

    /// <summary>Adds a <see cref="Task.Delay"/> before each <see cref="UploadRangeAsync"/> call.</summary>
    public void SetRangeLatency(TimeSpan delay) => _rangeLatency = delay;

    /// <summary>Resets all injected faults to defaults (no failures, no latency).</summary>
    public void Reset()
    {
        _failNextRangeCount = 0;
        _failNextRangeCode = ErrorCode.UploadFailed;
        _expiryAfterRanges = int.MaxValue;
        _rangesUploaded = 0;
        _rangeLatency = TimeSpan.Zero;
    }

    // ── IStorageProvider ─────────────────────────────────────────────────────────────────────

    public Task<Result<UploadSession>> BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct) =>
        _inner.BeginUploadAsync(remoteName, totalBytes, ct);

    public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct) =>
        _inner.GetUploadedBytesAsync(session, ct);

    public async Task<Result> UploadRangeAsync(
        UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_rangeLatency > TimeSpan.Zero)
        {
            await Task.Delay(_rangeLatency, ct).ConfigureAwait(false);
        }

        if (_rangesUploaded >= _expiryAfterRanges)
        {
            return Result.Fail(ErrorCode.UploadSessionExpired, "Injected session expiry.");
        }

        if (_failNextRangeCount > 0)
        {
            _failNextRangeCount--;
            return Result.Fail(_failNextRangeCode, $"Injected failure: {_failNextRangeCode}.");
        }

        var result = await _inner.UploadRangeAsync(session, offset, data, ct).ConfigureAwait(false);
        if (result.Success)
        {
            _rangesUploaded++;
        }

        return result;
    }

    public Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct) =>
        _inner.FinaliseUploadAsync(session, ct);

    public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct) =>
        _inner.AbortUploadAsync(session, ct);

    public Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct) =>
        _inner.DownloadAsync(remoteId, ct);

    public Task<Result> DeleteAsync(string remoteId, CancellationToken ct) =>
        _inner.DeleteAsync(remoteId, ct);

    public Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct) =>
        _inner.ExistsAsync(remoteId, ct);

    public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct) =>
        _inner.ListAsync(prefix, ct);

    public Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct) =>
        _inner.CheckHealthAsync(ct);

    public Task<Result<long>> GetUsedBytesAsync(CancellationToken ct) =>
        _inner.GetUsedBytesAsync(ct);

    public Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct) =>
        _inner.GetQuotaBytesAsync(ct);
}
