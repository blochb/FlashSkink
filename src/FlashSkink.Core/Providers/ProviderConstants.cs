namespace FlashSkink.Core.Providers;

/// <summary>
/// Cross-provider layout constants shared by every cloud provider adapter introduced in
/// §4.3–4.5. Cross-cutting decision 5 of phase-4-providers: the layout is fixed at V1 and lives
/// here so changing it (which would orphan previously-uploaded blobs) requires touching a single
/// file.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope.</strong> Cloud providers prepend <see cref="CloudRootFolderName"/> to every
/// remote name they pass through their SDK; FileSystem providers use their configured
/// <c>rootPath</c> directly (no prepend). The subpath constants
/// (<see cref="WitnessPrefix"/>, <see cref="BrainMirrorPrefix"/>, <see cref="BlobsPrefix"/>) are
/// relative to whichever root the provider uses and are identical across providers — this is the
/// cognitive-consistency goal of the decision.
/// </para>
/// <para>
/// <strong>Existing literals.</strong> The constants are introduced here for §4.3–4.5 cloud
/// providers to consume. Existing call sites in <c>WitnessStore</c> and <c>BrainMirrorService</c>
/// continue to use their own string literals in §4.1; rewiring is a non-goal of this PR and lands
/// in §4.3 as a natural touch-point.
/// </para>
/// </remarks>
internal static class ProviderConstants
{
    /// <summary>Top-level folder name on cloud providers (Google Drive, Dropbox, OneDrive).</summary>
    public const string CloudRootFolderName = "FlashSkink Backup";

    /// <summary>Relative subpath of the witness file (<c>_witness/current.enc</c>).</summary>
    public const string WitnessSubpath = "_witness/current.enc";

    /// <summary>Relative subpath prefix used for listing the witness folder.</summary>
    public const string WitnessPrefix = "_witness/";

    /// <summary>Relative subpath prefix used for rolling brain mirrors (<c>_brain/{timestamp}.bin</c>).</summary>
    public const string BrainMirrorPrefix = "_brain/";

    /// <summary>Relative subpath prefix used for sharded encrypted blobs.</summary>
    public const string BlobsPrefix = "blobs/";
}
