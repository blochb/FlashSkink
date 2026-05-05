using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.IO;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Per-volume parameter object that carries the live brain connection, DEK view, volume-scoped
/// pipeline instances, repositories, and infrastructure that <see cref="WritePipeline"/> and
/// the future <c>FlashSkinkVolume</c> consume. Constructed once per volume open; disposed on
/// volume close.
/// </summary>
/// <remarks>
/// Ownership rules:
/// <list type="bullet">
///   <item><see cref="Sha256"/> and <see cref="Compression"/> are owned by this instance and are
///   disposed by <see cref="Dispose"/>.</item>
///   <item><see cref="BrainConnection"/> and <see cref="Dek"/> are <em>borrowed</em> from the
///   <c>VolumeSession</c> — they are <em>not</em> disposed here.</item>
///   <item>All other properties retain their prior owners.</item>
/// </list>
/// </remarks>
public sealed class VolumeContext : IDisposable
{
    private int _disposed;

    /// <summary>
    /// Maximum plaintext bytes per file. Equals <see cref="Array.MaxLength"/> (~2 GiB) so that
    /// the single-buffer allocation in <c>ReadIntoBufferAsync</c> never overflows an <c>int</c>
    /// cast. Aliases <see cref="CompressionService.MaxPlaintextBytes"/>.
    /// </summary>
    public static readonly long MaxPlaintextBytes = CompressionService.MaxPlaintextBytes;

    /// <summary>Open encrypted brain connection; lifetime owned by <c>VolumeSession</c>, not by this context.</summary>
    public SqliteConnection BrainConnection { get; }

    /// <summary>
    /// Borrowed view of the live 32-byte DEK; lifetime owned by <c>VolumeSession</c>.
    /// Do not zero; <c>VolumeSession.DisposeAsync</c> owns zeroing.
    /// </summary>
    public ReadOnlyMemory<byte> Dek { get; }

    /// <summary>Skink root path, e.g. <c>"E:\"</c> or <c>"/mnt/usb"</c>.</summary>
    public string SkinkRoot { get; }

    /// <summary>
    /// Volume-scoped SHA-256 incremental hasher; reused across write calls.
    /// Owned by this context; disposed by <see cref="Dispose"/>.
    /// </summary>
    public IncrementalHash Sha256 { get; }

    /// <summary>
    /// Volume-scoped AES-256-GCM pipeline; stateless — one <c>AesGcm</c> is allocated per
    /// call internally. Not disposed (no native handle).
    /// </summary>
    public CryptoPipeline Crypto { get; }

    /// <summary>
    /// Volume-scoped compression service; native Zstd codec handles are reused across calls.
    /// Owned by this context; disposed by <see cref="Dispose"/>.
    /// </summary>
    public CompressionService Compression { get; }

    /// <summary>Atomic blob writer; stateless beyond its logger. Not disposed.</summary>
    public AtomicBlobWriter BlobWriter { get; }

    /// <summary>
    /// Singleton <see cref="RecyclableMemoryStreamManager"/> for any pipeline stage that needs
    /// a growing memory stream (non-seekable source path in §14.1 stage 1). Not disposed.
    /// </summary>
    public RecyclableMemoryStreamManager StreamManager { get; }

    /// <summary>The notification bus that pipelines publish failure events to.</summary>
    public INotificationBus NotificationBus { get; }

    /// <summary>Repository for blob rows in the brain.</summary>
    public BlobRepository Blobs { get; }

    /// <summary>Repository for file and folder rows in the brain.</summary>
    public FileRepository Files { get; }

    /// <summary>Repository for WAL crash-recovery rows in the brain.</summary>
    public WalRepository Wal { get; }

    /// <summary>Repository for the user-facing activity audit trail.</summary>
    public ActivityLogRepository ActivityLog { get; }

    /// <summary>
    /// Constructs a <see cref="VolumeContext"/>. The caller transfers ownership of
    /// <paramref name="sha256"/> and <paramref name="compression"/> to this instance — both are
    /// disposed by <see cref="Dispose"/>. All other parameters retain their prior owners.
    /// </summary>
    public VolumeContext(
        SqliteConnection brainConnection,
        ReadOnlyMemory<byte> dek,
        string skinkRoot,
        IncrementalHash sha256,
        CryptoPipeline crypto,
        CompressionService compression,
        AtomicBlobWriter blobWriter,
        RecyclableMemoryStreamManager streamManager,
        INotificationBus notificationBus,
        BlobRepository blobs,
        FileRepository files,
        WalRepository wal,
        ActivityLogRepository activityLog)
    {
        BrainConnection = brainConnection;
        Dek = dek;
        SkinkRoot = skinkRoot;
        Sha256 = sha256;
        Crypto = crypto;
        Compression = compression;
        BlobWriter = blobWriter;
        StreamManager = streamManager;
        NotificationBus = notificationBus;
        Blobs = blobs;
        Files = files;
        Wal = wal;
        ActivityLog = activityLog;
    }

    /// <summary>
    /// Disposes the volume-scoped <see cref="Sha256"/> and <see cref="Compression"/> instances.
    /// Idempotent — safe to call multiple times. Does not dispose
    /// <see cref="BrainConnection"/> (owned by <c>VolumeSession</c>).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Sha256.Dispose();
        Compression.Dispose();
    }
}
