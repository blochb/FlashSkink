namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// EventArgs raised when the skink USB device is removed while a volume is open; carries
/// the skink root path so subscribers can surface a recovery prompt.
/// </summary>
public sealed class UsbRemovedEventArgs : EventArgs
{
    /// <summary>The skink root path that was removed (e.g. <c>"E:\"</c> or <c>"/mnt/usb"</c>).</summary>
    public string SkinkRoot { get; }

    /// <summary>Initialises a new <see cref="UsbRemovedEventArgs"/> with the given skink root path.</summary>
    public UsbRemovedEventArgs(string skinkRoot)
    {
        SkinkRoot = skinkRoot;
    }
}
