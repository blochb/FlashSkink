using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Identity;

/// <summary>
/// Identifies which of the two parallel conflict triggers fired during a session-begin
/// witness handshake. Both triggers are evaluated against every reachable tail; the first
/// trigger to fire wins for diagnostic attribution (the <c>ConflictingProviderId</c> and
/// <c>ConflictWitness</c> fields of <see cref="WitnessHandshakeOutcome"/>).
/// </summary>
internal enum ConflictTrigger
{
    /// <summary>
    /// No conflict trigger fired. Paired with <see cref="WitnessHandshakeOutcome.ConflictDetected"/>
    /// being <see langword="false"/> in the no-conflict outcome.
    /// </summary>
    None = 0,

    /// <summary>
    /// A tail's witness <c>Epoch</c> is at or beyond the local <c>newEpoch</c>: another
    /// holder has advanced the epoch to a value at or beyond ours. (Blueprint §19.7
    /// Trigger A.)
    /// </summary>
    EpochComparison = 1,

    /// <summary>
    /// A tail's witness <c>Epoch</c> is below the local <c>newEpoch</c> but carries
    /// <c>ConflictObserved = true</c> — the lagger-detection half of the two-sided fence.
    /// The other skink (the winner) detected a conflict against us in a prior session and
    /// stamped this marker so we learn about it on our next open even though our own epoch
    /// is now ahead. (Blueprint §19.7 Trigger B.)
    /// </summary>
    ConflictMarker = 2,
}

/// <summary>
/// Result of a single <see cref="WitnessHandshake.RunAsync"/> invocation.
/// <see cref="ConflictDetected"/> reflects ONLY the fresh-conflict signal from this
/// handshake (<see cref="Trigger"/> non-<see cref="ConflictTrigger.None"/>); the caller
/// composes it with the persisted <c>VolumeState</c> to decide whether to enter, remain
/// in, or auto-downgrade from fenced state.
/// </summary>
/// <param name="ConflictDetected">
/// <see langword="true"/> when at least one tail triggered a fresh conflict (epoch
/// comparison or conflict marker).
/// </param>
/// <param name="Trigger">
/// The trigger kind for the FIRST tail (in input order) that fired a conflict. Deterministic
/// for diagnostic attribution.
/// </param>
/// <param name="ConflictingProviderId">
/// The provider id of the first tail that triggered the conflict; <see langword="null"/>
/// when no conflict fired.
/// </param>
/// <param name="ConflictWitness">
/// The offending witness payload from <see cref="ConflictingProviderId"/>;
/// <see langword="null"/> when no conflict fired.
/// </param>
/// <param name="TailsRead">
/// Number of tails whose <c>TryReadAsync</c> returned <c>Ok(...)</c> (with or without a
/// payload). Used by the caller's auto-downgrade gate: positive evidence requires at least
/// one tail actually read.
/// </param>
/// <param name="TailsWritten">
/// Number of tails where the witness write succeeded. A write failure on one tail does NOT
/// fail the handshake — it is an upload problem, not a split-brain.
/// </param>
internal readonly record struct WitnessHandshakeOutcome(
    bool ConflictDetected,
    ConflictTrigger Trigger,
    string? ConflictingProviderId,
    WitnessPayload? ConflictWitness,
    int TailsRead,
    int TailsWritten);

/// <summary>
/// Runs the session-begin witness handshake against every reachable tail: reads each tail's
/// witness file, evaluates the two parallel conflict triggers, and writes a fresh witness
/// to every accessible tail. (Blueprint §19.6, dev plan §3.5.2.)
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read phase — sequential, no parallel I/O.</strong> Tails are iterated in input
/// order so the "first triggering tail wins" attribution is deterministic. Sequencing the
/// later write phase through one tail at a time keeps the "winner writes to all tails" step
/// from racing itself in the rare overlap case.
/// </para>
/// <para>
/// <strong>Two parallel conflict triggers, both evaluated per tail.</strong> Trigger A
/// (epoch comparison): <c>witness.Epoch &gt;= newEpoch</c> from any reachable tail.
/// Trigger B (conflict marker): <c>witness.Epoch &lt; newEpoch</c> AND
/// <c>witness.ConflictObserved == true</c> from any reachable tail. Either trigger sets
/// <see cref="WitnessHandshakeOutcome.ConflictDetected"/> to <see langword="true"/>; the
/// first tail to fire either trigger wins the attribution fields. The loop does NOT break
/// on the first conflict — every tail is still read so <c>TailsRead</c> reflects the full
/// reachable set (needed by the caller's auto-downgrade gate).
/// </para>
/// <para>
/// <strong>Write phase — Principle 17.</strong> Once the read phase decides the new
/// witness's <c>ConflictObserved</c> value, the write to every accessible tail uses
/// <see cref="CancellationToken.None"/>: cancellation observed mid-write would leave half
/// the tails carrying the new fence marker and the other half carrying the stale one,
/// defeating the two-sided fence's "any tail surfaces it" guarantee.
/// </para>
/// </remarks>
internal sealed class WitnessHandshake
{
    private readonly WitnessStore _store;
    private readonly ILogger<WitnessHandshake> _logger;

    /// <summary>Creates a <see cref="WitnessHandshake"/>.</summary>
    internal WitnessHandshake(WitnessStore store, ILogger<WitnessHandshake> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Reads every tail's witness, evaluates the two conflict triggers, and writes a fresh
    /// witness to every accessible tail.
    /// </summary>
    /// <param name="tails">Tails to handshake against. Empty list returns no-conflict, zero reads, zero writes.</param>
    /// <param name="volumeId">The local volume's <c>Settings["VolumeID"]</c>; witnesses whose <c>VolumeId</c> field differs are skipped.</param>
    /// <param name="newEpoch">The just-incremented local <c>VolumeEpoch</c>; conflict comparison key.</param>
    /// <param name="appVersion">The current process's app-version string, captured into the new witness payload.</param>
    /// <param name="dek">The 32-byte DEK used to encrypt and decrypt witnesses.</param>
    /// <param name="alreadyFenced">
    /// When <see langword="true"/>, the new witness is written with <c>ConflictObserved = true</c>
    /// regardless of whether this handshake observed a fresh conflict (perpetuates the two-sided
    /// fence until <c>PromoteAsync</c> resets the local state).
    /// </param>
    /// <param name="ct">Cancellation token; observed at method entry and during the read phase. The write phase uses <see cref="CancellationToken.None"/>.</param>
    internal async Task<Result<WitnessHandshakeOutcome>> RunAsync(
        IReadOnlyList<(string ProviderId, IStorageProvider Provider)> tails,
        string volumeId,
        long newEpoch,
        string appVersion,
        ReadOnlyMemory<byte> dek,
        bool alreadyFenced,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            bool conflictDetected = false;
            ConflictTrigger trigger = ConflictTrigger.None;
            string? conflictingProviderId = null;
            WitnessPayload? conflictWitness = null;
            int tailsRead = 0;
            // Tails the read-phase probe proved unreachable. The write phase consults this
            // set to skip the doomed write — without it, every unreachable tail would emit
            // a "witness write failed" warning indistinguishable from a real upload problem.
            var unreachableTails = new HashSet<string>(StringComparer.Ordinal);

            // ── Read phase ────────────────────────────────────────────────────
            foreach (var (providerId, provider) in tails)
            {
                // Reachability probe — only count tails that respond. WitnessStore.TryReadAsync
                // maps offline failures to Ok(null) (matching its "no information from this tail"
                // contract), which would conflate offline with "tail is fine, no witness yet" in
                // the TailsRead counter. The auto-downgrade gate in the caller needs the
                // distinction (it requires positive evidence of a clean tail before downgrading
                // a stale Fenced row), so we probe first.
                var probe = await provider.ListAsync(
                    WitnessStore.WitnessRemotePrefix, ct).ConfigureAwait(false);
                if (!probe.Success && WitnessStore.OfflineErrorCodes.Contains(probe.Error!.Code))
                {
                    _logger.LogDebug(
                        "Tail {ProviderId} unreachable for handshake probe ({Code}); skipping read, not counting toward TailsRead.",
                        providerId, probe.Error.Code);
                    unreachableTails.Add(providerId);
                    continue;
                }

                var readResult = await _store.TryReadAsync(provider, dek, ct).ConfigureAwait(false);
                if (!readResult.Success)
                {
                    // TryReadAsync only Fails on Cancelled or Unknown. Cancelled re-throws via
                    // the outer catch (ct is still observed in the loop). Unknown is logged at
                    // Debug and counted as "not read" — this tail provides no information.
                    if (readResult.Error!.Code == ErrorCode.Cancelled)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    _logger.LogDebug(
                        "Witness read failed on tail {ProviderId}: {Code}; treating as no information.",
                        providerId, readResult.Error.Code);
                    continue;
                }

                var payload = readResult.Value;
                if (payload is null)
                {
                    // The probe succeeded, so the tail is genuinely reachable; it just has no
                    // (parseable) witness yet. This IS positive evidence for the auto-downgrade
                    // gate ("we reached this tail and confirmed no conflict signal lives here").
                    tailsRead++;
                    continue;
                }

                // Volume-ID validation. The DEK-binding guarantee makes a mismatch
                // near-impossible (a wrong DEK would have failed decrypt at the store layer),
                // but defensive per Blueprint §19.6 — treated as no information from this tail.
                // A mismatched-VolumeId witness is NOT evidence about this volume; do not
                // increment tailsRead, so the auto-downgrade gate cannot use it as confirmation.
                if (!string.Equals(payload.Value.VolumeId, volumeId, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Witness on tail {ProviderId} carries VolumeId={WitnessVolumeId}, expected {ExpectedVolumeId}; treating as no information.",
                        providerId, payload.Value.VolumeId, volumeId);
                    continue;
                }

                // Tail is reachable AND carries a witness bound to THIS volume — positive
                // evidence regardless of whether a trigger fires below.
                tailsRead++;

                // Trigger A — epoch comparison.
                if (payload.Value.Epoch >= newEpoch && !conflictDetected)
                {
                    conflictDetected = true;
                    trigger = ConflictTrigger.EpochComparison;
                    conflictingProviderId = providerId;
                    conflictWitness = payload;
                    continue;
                }

                // Trigger B — conflict marker (lagger detection).
                if (payload.Value.Epoch < newEpoch && payload.Value.ConflictObserved && !conflictDetected)
                {
                    conflictDetected = true;
                    trigger = ConflictTrigger.ConflictMarker;
                    conflictingProviderId = providerId;
                    conflictWitness = payload;
                    continue;
                }
            }

            // ── Write phase (Principle 17 — CancellationToken.None below) ─────
            bool stampConflictMarker = conflictDetected || alreadyFenced;
            var newPayload = WitnessPayload.ForNewSession(volumeId, newEpoch, appVersion)
                with
            { ConflictObserved = stampConflictMarker };

            int tailsWritten = 0;
            foreach (var (providerId, provider) in tails)
            {
                if (unreachableTails.Contains(providerId))
                {
                    // Probe already proved this tail offline; a write attempt would re-fail
                    // and emit a noisy warning. The next session-begin handshake retries.
                    continue;
                }

                var writeResult = await _store.WriteAsync(
                    provider, dek, newPayload, CancellationToken.None).ConfigureAwait(false);
                if (writeResult.Success)
                {
                    tailsWritten++;
                }
                else
                {
                    // A write failure on a tail that was reachable at probe time is a genuine
                    // upload problem (transient blip after the probe, quota, perms) — logged
                    // and counted separately; the handshake proceeds.
                    _logger.LogWarning(
                        "Witness write failed on tail {ProviderId}: {Code}.",
                        providerId, writeResult.Error!.Code);
                }
            }

            return Result<WitnessHandshakeOutcome>.Ok(new WitnessHandshakeOutcome(
                ConflictDetected: conflictDetected,
                Trigger: trigger,
                ConflictingProviderId: conflictingProviderId,
                ConflictWitness: conflictWitness,
                TailsRead: tailsRead,
                TailsWritten: tailsWritten));
        }
        catch (OperationCanceledException ex)
        {
            return Result<WitnessHandshakeOutcome>.Fail(
                ErrorCode.Cancelled, "Witness handshake was cancelled.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during witness handshake.");
            return Result<WitnessHandshakeOutcome>.Fail(
                ErrorCode.Unknown, "Unexpected error during witness handshake.", ex);
        }
    }
}
