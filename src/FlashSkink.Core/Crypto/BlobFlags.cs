namespace FlashSkink.Core.Crypto;

/// <summary>
/// Bitmask recording which optional pipeline stages were applied to a blob.
/// Bit positions match blueprint §13.6.
/// </summary>
[Flags]
public enum BlobFlags : ushort
{
    /// <summary>No optional stages applied.</summary>
    None = 0,

    /// <summary>Plaintext was compressed with LZ4 before encryption.</summary>
    CompressedLz4 = 1 << 0,

    /// <summary>Plaintext was compressed with Zstd before encryption.</summary>
    CompressedZstd = 1 << 1,
}
