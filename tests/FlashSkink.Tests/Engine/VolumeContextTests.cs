using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using FlashSkink.Tests.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IO;
using Xunit;

namespace FlashSkink.Tests.Engine;

public sealed class VolumeContextTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection;

    public VolumeContextTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() { }

    private static VolumeContext MakeContext(
        SqliteConnection conn,
        IncrementalHash? sha256 = null,
        CompressionService? compression = null)
    {
        var wal = new WalRepository(conn, NullLogger<WalRepository>.Instance);
        var blobs = new BlobRepository(conn, NullLogger<BlobRepository>.Instance);
        var files = new FileRepository(conn, wal, NullLogger<FileRepository>.Instance);
        var activity = new ActivityLogRepository(conn, NullLogger<ActivityLogRepository>.Instance);

        return new VolumeContext(
            brainConnection: conn,
            dek: new byte[32].AsMemory(),
            skinkRoot: "E:\\",
            sha256: sha256 ?? IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            crypto: new CryptoPipeline(),
            compression: compression ?? new CompressionService(),
            blobWriter: new AtomicBlobWriter(NullLogger<AtomicBlobWriter>.Instance),
            streamManager: new RecyclableMemoryStreamManager(),
            notificationBus: new NullNotificationBus(),
            blobs: blobs,
            files: files,
            wal: wal,
            activityLog: activity);
    }

    [Fact]
    public void Construct_ExposesAllInjectedFields()
    {
        var dek = new byte[32];
        dek[0] = 0xAB;
        var skinkRoot = "/mnt/usb";
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crypto = new CryptoPipeline();
        var compression = new CompressionService();
        var writer = new AtomicBlobWriter(NullLogger<AtomicBlobWriter>.Instance);
        var sm = new RecyclableMemoryStreamManager();
        var bus = new NullNotificationBus();
        var wal = new WalRepository(_connection, NullLogger<WalRepository>.Instance);
        var blobs = new BlobRepository(_connection, NullLogger<BlobRepository>.Instance);
        var files = new FileRepository(_connection, wal, NullLogger<FileRepository>.Instance);
        var activity = new ActivityLogRepository(_connection, NullLogger<ActivityLogRepository>.Instance);

        using var ctx = new VolumeContext(
            _connection, dek.AsMemory(), skinkRoot, sha256, crypto, compression,
            writer, sm, bus, blobs, files, wal, activity);

        Assert.Same(_connection, ctx.BrainConnection);
        Assert.Equal(dek, ctx.Dek.ToArray());
        Assert.Equal(skinkRoot, ctx.SkinkRoot);
        Assert.Same(sha256, ctx.Sha256);
        Assert.Same(crypto, ctx.Crypto);
        Assert.Same(compression, ctx.Compression);
        Assert.Same(writer, ctx.BlobWriter);
        Assert.Same(sm, ctx.StreamManager);
        Assert.Same(bus, ctx.NotificationBus);
        Assert.Same(blobs, ctx.Blobs);
        Assert.Same(files, ctx.Files);
        Assert.Same(wal, ctx.Wal);
        Assert.Same(activity, ctx.ActivityLog);
    }

    [Fact]
    public void Dispose_DoesNotDisposeBrainConnection()
    {
        var ctx = MakeContext(_connection);

        ctx.Dispose();

        // The connection must still be usable (owned by VolumeSession, not VolumeContext).
        var state = _connection.State;
        Assert.Equal(System.Data.ConnectionState.Open, state);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var compression = new CompressionService();
        var ctx = MakeContext(_connection, sha256, compression);

        ctx.Dispose(); // first dispose
        var ex = Record.Exception(() => ctx.Dispose()); // second dispose — must not throw

        Assert.Null(ex);
    }

    [Fact]
    public void MaxPlaintextBytes_EqualsArrayMaxLength()
    {
        Assert.Equal((long)Array.MaxLength, VolumeContext.MaxPlaintextBytes);
    }
}

/// <summary>No-op notification bus for test infrastructure.</summary>
file sealed class NullNotificationBus : INotificationBus
{
    public ValueTask PublishAsync(Notification notification, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
