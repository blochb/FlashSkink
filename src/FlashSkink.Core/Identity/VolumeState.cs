namespace FlashSkink.Core.Identity;

/// <summary>
/// Persistent operational state of a FlashSkink volume. Stored in
/// <c>Settings["VolumeState"]</c> as the enum's <see cref="object.ToString"/> form
/// (e.g. <c>"Normal"</c>, <c>"Fenced"</c>); an absent or unparseable row is treated as
/// <see cref="Normal"/>. (Blueprint §19.7 — added in dev plan §3.5.2; the enum is defined
/// here in §3.5.1 so that <c>BackfillAndStampOnOpenAsync</c> can return it from this PR
/// onward.)
/// </summary>
/// <remarks>
/// The semantic <i>enforcement</i> of the <see cref="Fenced"/> state — blocking Phase 2
/// uploads, surfacing notifications, persisting the row, providing the
/// <c>FlashSkinkVolume.PromoteAsync</c> exit — lands in dev plan §3.5.2 and §3.5.3.
/// This file is the type definition only.
/// </remarks>
public enum VolumeState
{
    /// <summary>
    /// Default state. No split-brain conflict has been detected against any tail; Phase 2
    /// uploads proceed normally.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// A prior session-begin witness handshake detected a divergent clone on at least one
    /// reachable tail. Phase 2 uploads are blocked on every tail until the user resolves
    /// the conflict via <c>FlashSkinkVolume.PromoteAsync</c>. Phase 1 writes and reads
    /// continue to function. (Blueprint §19.7.)
    /// </summary>
    Fenced = 1,
}
