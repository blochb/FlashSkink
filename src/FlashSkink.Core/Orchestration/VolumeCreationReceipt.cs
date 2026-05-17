using FlashSkink.Core.Abstractions.Crypto;

namespace FlashSkink.Core.Orchestration;

/// <summary>
/// Receipt returned exactly once from <see cref="FlashSkinkVolume.CreateAsync"/>.
/// Carries the open volume and the 24-word BIP-39 recovery phrase generated
/// at setup.
/// </summary>
/// <remarks>
/// <para>
/// The phrase is <b>not</b> persisted anywhere by FlashSkink — neither on the
/// skink nor on any tail (see blueprint §18.8, §29 Decision A16). Losing this
/// receipt without recording the phrase forfeits the only out-of-band recovery
/// path for the volume.
/// </para>
/// <para>
/// The <see cref="Volume"/> and <see cref="RecoveryPhrase"/> have intentionally
/// distinct lifetimes: the volume lives until the user closes the skink (often
/// many operations later); the phrase is meant to be displayed once at setup
/// and then disposed. The receipt itself is not <see cref="IDisposable"/> so
/// callers do not accidentally couple the two lifetimes — dispose
/// <see cref="RecoveryPhrase"/> as soon as it has been shown and recorded,
/// then continue using <see cref="Volume"/>.
/// </para>
/// </remarks>
/// <param name="Volume">The open volume, ready for use. Caller owns disposal.</param>
/// <param name="RecoveryPhrase">
/// The 24-word BIP-39 phrase. Caller owns disposal; chars are zeroed on
/// <see cref="Crypto.RecoveryPhrase.Dispose"/>.
/// </param>
public sealed record VolumeCreationReceipt(
    FlashSkinkVolume Volume,
    RecoveryPhrase RecoveryPhrase);
