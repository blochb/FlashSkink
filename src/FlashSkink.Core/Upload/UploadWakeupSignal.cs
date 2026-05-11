using System.Threading.Channels;

namespace FlashSkink.Core.Upload;

/// <summary>
/// Edge-triggered, coalescing wakeup primitive shared between the upload-queue orchestrator,
/// its per-tail workers, and external producers (the <c>WritePipeline</c> post-commit hook
/// in §3.6). Backed by a capacity-1 <see cref="Channel{T}"/> with
/// <see cref="BoundedChannelFullMode.DropWrite"/>: a backed-up pulse is the same as a pending
/// pulse, so there is no benefit to buffering more than one. Blueprint §15.8 (worker wakeup).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Threading.</strong> Many producers may call <see cref="Pulse"/> concurrently
/// (orchestrator + post-commit hook from <c>WritePipeline</c>). Many consumers may call
/// <see cref="WaitAsync"/> concurrently (orchestrator + one worker per tail). The channel's
/// <c>SingleReader = false</c> / <c>SingleWriter = false</c> options enable both.
/// </para>
/// <para>
/// <strong>Cancellation contract.</strong> <see cref="WaitAsync"/> returns a
/// <see cref="ValueTask"/> rather than a <see cref="Results.Result"/> wrapper for the same
/// reason <c>IClock.Delay</c> does (§3.2): cancellation is the only failure mode and is
/// already in-band as <see cref="OperationCanceledException"/>. The caller's catch-first
/// ladder maps to <c>Result.Fail(Cancelled)</c> at its public boundary per Principle 14.
/// </para>
/// </remarks>
public sealed class UploadWakeupSignal
{
    private readonly Channel<byte> _channel;

    /// <summary>Creates a new wakeup signal with no tokens buffered.</summary>
    public UploadWakeupSignal()
    {
        _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Writes a single wakeup token. Idempotent: if a token is already buffered, the channel's
    /// <see cref="BoundedChannelFullMode.DropWrite"/> policy silently drops the new write.
    /// Never blocks. Safe to call after <see cref="Complete"/>: <c>TryWrite</c> returns
    /// <see langword="false"/> on a completed channel, which is observable only by the caller's
    /// <see cref="WaitAsync"/> already returning.
    /// </summary>
    public void Pulse() => _channel.Writer.TryWrite(0);

    /// <summary>
    /// Asynchronously reads one wakeup token. Completes when <see cref="Pulse"/> is called,
    /// throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled,
    /// or returns normally when the channel is completed during dispose (the caller is expected
    /// to check <paramref name="ct"/> and exit its loop).
    /// </summary>
    /// <param name="ct">Cancellation token; cancellation propagates as <see cref="OperationCanceledException"/>.</param>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        try
        {
            _ = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Completed during dispose; caller checks ct and exits its outer loop.
            // OperationCanceledException is allowed to propagate (Principle 14).
        }
    }

    /// <summary>
    /// Completes the underlying channel so any pending <see cref="WaitAsync"/> returns and
    /// subsequent <see cref="Pulse"/> calls are silent no-ops. Called once by
    /// <c>UploadQueueService.DisposeAsync</c>.
    /// </summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
