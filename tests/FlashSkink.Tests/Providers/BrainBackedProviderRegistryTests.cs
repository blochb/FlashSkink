using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Orchestration;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Providers.GoogleDrive;
using FlashSkink.Core.Providers.Setup;
using FlashSkink.Tests.Engine;
using FlashSkink.Tests.Metadata;
using FlashSkink.Tests.Orchestration;
using FlashSkink.Tests.Providers.GoogleDrive;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers;

/// <summary>
/// Unit tests for <see cref="BrainBackedProviderRegistry"/>. PR §4.1.
/// </summary>
public sealed class BrainBackedProviderRegistryTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BrainAccess _brain;
    private readonly string _tempRoot;
    private readonly byte[] _dek;

    public BrainBackedProviderRegistryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _brain = new BrainAccess(_connection);
        _tempRoot = Path.Combine(Path.GetTempPath(), "flashskink-bbpr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _dek = new byte[32]; // not used in §4.1 (FileSystem rows don't decrypt anything)
    }

    public async Task InitializeAsync()
    {
        await BrainTestHelper.ApplySchemaAsync(_connection);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _brain.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _connection.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private string MakeTailDir(string suffix)
    {
        var p = Path.Combine(_tempRoot, suffix);
        Directory.CreateDirectory(p);
        return p;
    }

    private void InsertProvider(
        string providerId,
        string providerType,
        string displayName,
        string? providerConfig,
        bool isActive = true)
    {
        _connection.Execute(
            """
            INSERT INTO Providers
                (ProviderID, ProviderType, DisplayName, ProviderConfig, HealthStatus, AddedUtc, IsActive)
            VALUES
                (@Id, @Type, @Name, @Config, 'Healthy', @AddedUtc, @IsActive)
            """,
            new
            {
                Id = providerId,
                Type = providerType,
                Name = displayName,
                Config = providerConfig,
                AddedUtc = DateTime.UtcNow.ToString("O"),
                IsActive = isActive ? 1 : 0,
            });
    }

    private void InsertGoogleDriveProvider(
        string providerId,
        string displayName,
        string? clientId,
        byte[]? encryptedToken,
        byte[]? encryptedClientSecret,
        string? providerConfig,
        bool isActive = true)
    {
        _connection.Execute(
            """
            INSERT INTO Providers
                (ProviderID, ProviderType, DisplayName, ClientID,
                 EncryptedToken, EncryptedClientSecret, ProviderConfig,
                 HealthStatus, AddedUtc, IsActive)
            VALUES
                (@Id, 'google-drive', @Name, @ClientId,
                 @EncToken, @EncSecret, @Config,
                 'Healthy', @AddedUtc, @IsActive)
            """,
            new
            {
                Id = providerId,
                Name = displayName,
                ClientId = clientId,
                EncToken = encryptedToken,
                EncSecret = encryptedClientSecret,
                Config = providerConfig,
                AddedUtc = DateTime.UtcNow.ToString("O"),
                IsActive = isActive ? 1 : 0,
            });
    }

    private async Task<BrainBackedProviderRegistry> BuildAsync(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var result = await BrainBackedProviderRegistry.CreateAsync(
            _brain, _dek, loggerFactory, CancellationToken.None);
        Assert.True(result.Success);
        return result.Value!;
    }

    private async Task<BrainBackedProviderRegistry> BuildAsync(
        IGoogleDriveClientFactory googleDriveFactory,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var result = await BrainBackedProviderRegistry.CreateAsync(
            _brain, _dek, googleDriveFactory, loggerFactory, CancellationToken.None);
        Assert.True(result.Success);
        return result.Value!;
    }

    // ── CreateAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_EmptyProviders_ReturnsEmptyRegistry()
    {
        await using var registry = await BuildAsync();

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
    }

    [Fact]
    public async Task CreateAsync_OneFileSystemRow_RegistryHasOneProvider()
    {
        var tail = MakeTailDir("tail-fs");
        InsertProvider("fs-1", "filesystem", "Local Folder",
            $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""");

        await using var registry = await BuildAsync();

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Single(ids);
        Assert.Equal("fs-1", ids[0]);

        var providerResult = await registry.GetAsync("fs-1", CancellationToken.None);
        Assert.True(providerResult.Success);
        Assert.Equal("fs-1", providerResult.Value!.ProviderID);
        Assert.Equal("filesystem", providerResult.Value!.ProviderType);
        Assert.Equal("Local Folder", providerResult.Value!.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_TwoFileSystemRows_RegistryHasBoth()
    {
        var tailA = MakeTailDir("tail-A");
        var tailB = MakeTailDir("tail-B");
        InsertProvider("fs-A", "filesystem", "Tail A",
            $$"""{"rootPath":"{{tailA.Replace("\\", "\\\\")}}"}""");
        InsertProvider("fs-B", "filesystem", "Tail B",
            $$"""{"rootPath":"{{tailB.Replace("\\", "\\\\")}}"}""");

        await using var registry = await BuildAsync();

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Equal(2, ids.Count);
        Assert.Contains("fs-A", ids);
        Assert.Contains("fs-B", ids);
    }

    [Fact]
    public async Task CreateAsync_OneRowWithInvalidRootPath_LogsWarningAndSkips()
    {
        var missing = Path.Combine(_tempRoot, "no-such-dir");
        InsertProvider("fs-broken", "filesystem", "Broken",
            $$"""{"rootPath":"{{missing.Replace("\\", "\\\\")}}"}""");

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("failed to construct", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneRowWithMalformedProviderConfig_LogsWarningAndSkips()
    {
        InsertProvider("fs-malformed", "filesystem", "Malformed", "{not valid json");

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("malformed ProviderConfig", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneRowWithUnknownProviderType_LogsWarningAndSkips()
    {
        InsertProvider("unknown-1", "totally-unknown", "Unknown", providerConfig: null);

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("Unknown provider type", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dropbox")]
    [InlineData("onedrive")]
    public async Task CreateAsync_OneRowWithUnsupportedCloudProviderType_LogsWarningAndSkips(string providerType)
    {
        InsertProvider($"cloud-{providerType}", providerType, "Cloud Tail", providerConfig: null);

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("not yet supported", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_InactiveRow_NotIncluded()
    {
        var tail = MakeTailDir("tail-inactive");
        InsertProvider("fs-inactive", "filesystem", "Inactive",
            $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""",
            isActive: false);

        await using var registry = await BuildAsync();

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
    }

    [Fact]
    public async Task CreateAsync_CancellationRequested_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await BrainBackedProviderRegistry.CreateAsync(
            _brain, _dek, NullLoggerFactory.Instance, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task CreateAsync_MixedRows_ActiveFileSystemKept_OthersSkipped()
    {
        var tail = MakeTailDir("tail-mixed");
        InsertProvider("fs-ok", "filesystem", "OK",
            $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""");
        InsertProvider("cloud-x", "google-drive", "Drive", providerConfig: null);
        InsertProvider("weird", "totally-unknown", "Weird", providerConfig: null);

        await using var registry = await BuildAsync();

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Single(ids);
        Assert.Equal("fs-ok", ids[0]);
    }

    // ── google-drive dispatch arm (§4.3) ─────────────────────────────────────────────────────

    private const string GdriveConfigJson = "{\"folderId\":\"folder-fake-id\",\"folderName\":\"FlashSkink Backup\"}";

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_WithAllCredentials_ConstructsProvider()
    {
        var token = ProviderTokenCrypto.Encrypt("rt-xyz", _dek);
        var secret = ProviderTokenCrypto.Encrypt("csec-xyz", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive", "cid", token, secret, GdriveConfigJson);

        var factory = new FakeGoogleDriveClientFactory();
        await using var registry = await BuildAsync(factory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Single(ids);
        Assert.Equal("gd-1", ids[0]);

        var providerResult = await registry.GetAsync("gd-1", CancellationToken.None);
        Assert.True(providerResult.Success);
        Assert.Equal("google-drive", providerResult.Value!.ProviderType);
        Assert.Equal("cid", factory.LastClientId);
        Assert.Equal("csec-xyz", factory.LastClientSecret);
        Assert.Equal("rt-xyz", factory.LastRefreshToken);
    }

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_MissingClientId_LogsWarningAndSkips()
    {
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        var secret = ProviderTokenCrypto.Encrypt("cs", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive",
            clientId: null, encryptedToken: token, encryptedClientSecret: secret,
            providerConfig: GdriveConfigJson);

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(new FakeGoogleDriveClientFactory(), logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("no ClientID", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_MissingEncryptedToken_LogsWarningAndSkips()
    {
        var secret = ProviderTokenCrypto.Encrypt("cs", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive", "cid",
            encryptedToken: null, encryptedClientSecret: secret,
            providerConfig: GdriveConfigJson);

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(new FakeGoogleDriveClientFactory(), logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("no EncryptedToken", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_MissingEncryptedClientSecret_LogsWarningAndSkips()
    {
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive", "cid",
            encryptedToken: token, encryptedClientSecret: null,
            providerConfig: GdriveConfigJson);

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(new FakeGoogleDriveClientFactory(), logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("no EncryptedClientSecret", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_DecryptClientSecretFails_LogsWarningAndSkips()
    {
        // Token encrypted with the correct DEK; secret encrypted with a different DEK.
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        var otherDek = new byte[32];
        otherDek[0] = 0xFF;
        var secret = ProviderTokenCrypto.Encrypt("cs", otherDek);

        InsertGoogleDriveProvider("gd-1", "Drive", "cid", token, secret, GdriveConfigJson);

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(new FakeGoogleDriveClientFactory(), logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("could not decrypt client secret",
            logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_MalformedProviderConfig_LogsWarningAndSkips()
    {
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        var secret = ProviderTokenCrypto.Encrypt("cs", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive", "cid", token, secret, providerConfig: "{not json");

        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(new FakeGoogleDriveClientFactory(), logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("malformed ProviderConfig",
            logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OneGoogleDriveRow_FactoryFails_LogsWarningAndSkips()
    {
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        var secret = ProviderTokenCrypto.Encrypt("cs", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive", "cid", token, secret, GdriveConfigJson);

        var factory = new FakeGoogleDriveClientFactory
        {
            CreateResultOverride = Result<GoogleDriveClientBundle>.Fail(
                ErrorCode.Unknown, "factory boom"),
        };
        var logFactory = new ListLoggerFactory();
        await using var registry = await BuildAsync(factory, logFactory);

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Empty(ids);
        Assert.Contains("failed to construct", logFactory.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_MixedFileSystemAndGoogleDriveRows_BothLoaded()
    {
        var tail = MakeTailDir("tail-mixed-gd");
        InsertProvider("fs-A", "filesystem", "Tail A",
            $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""");

        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        var secret = ProviderTokenCrypto.Encrypt("cs", _dek);
        InsertGoogleDriveProvider("gd-1", "Drive", "cid", token, secret, GdriveConfigJson);

        await using var registry = await BuildAsync(new FakeGoogleDriveClientFactory());

        var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Equal(2, ids.Count);
        Assert.Contains("fs-A", ids);
        Assert.Contains("gd-1", ids);
    }

    // ── GetAsync ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsProviderUnreachable()
    {
        await using var registry = await BuildAsync();

        var result = await registry.GetAsync("does-not-exist", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DisposesEveryAdapter()
    {
        // Construct a registry with two synthetic adapters via reflection-free injection: we
        // can't easily inject through CreateAsync, but the internal constructor lets us. The
        // class is internal-visible to FlashSkink.Tests via [InternalsVisibleTo].
        var registry = new BrainBackedProviderRegistry(
            NullLoggerFactory.Instance.CreateLogger<BrainBackedProviderRegistry>());

        var a = new DisposableFakeProvider("a");
        var b = new DisposableFakeProvider("b");
        TestSeed(registry, "a", a);
        TestSeed(registry, "b", b);

        await registry.DisposeAsync();

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var registry = new BrainBackedProviderRegistry(
            NullLoggerFactory.Instance.CreateLogger<BrainBackedProviderRegistry>());

        var a = new DisposableFakeProvider("a");
        TestSeed(registry, "a", a);

        await registry.DisposeAsync();
        await registry.DisposeAsync();   // must not throw

        Assert.Equal(1, a.DisposeCallCount);
    }

    /// <summary>
    /// Reflection-based seeder for the internal <c>_adapters</c> dictionary. Used only by the
    /// disposal tests above so they don't have to go through a real brain.
    /// </summary>
    private static void TestSeed(BrainBackedProviderRegistry registry, string id, IStorageProvider provider)
    {
        var field = typeof(BrainBackedProviderRegistry).GetField(
            "_adapters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, IStorageProvider>)field.GetValue(registry)!;
        dict[id] = provider;
    }

    // ── Integration: FlashSkinkVolume.OpenAsync uses BrainBackedProviderRegistry by default ──

    [Fact]
    public async Task VolumeOpen_NoExplicitRegistry_UsesBrainBackedRegistry()
    {
        var skinkRoot = Path.Combine(_tempRoot, "skink-vol-open");
        Directory.CreateDirectory(skinkRoot);
        const string password = "vol-open-test-password";

        // Create + dispose volume so the brain + vault land on disk.
        var createOptions = new VolumeCreationOptions
        {
            LoggerFactory = NullLoggerFactory.Instance,
            NotificationBus = new RecordingNotificationBus(),
        };
        var createReceipt = (await FlashSkinkVolume.CreateAsync(
            skinkRoot, password, createOptions)).Value!;
        try { await createReceipt.Volume.DisposeAsync(); }
        finally { createReceipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        // Insert a FileSystem Providers row directly into the on-disk brain.
        var tail = MakeTailDir("tail-vol-open");
        await using (var conn = await OrchestrationTestHelper.OpenBrainConnectionAsync(skinkRoot, password))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO Providers
                    (ProviderID, ProviderType, DisplayName, ProviderConfig, HealthStatus, AddedUtc, IsActive)
                VALUES
                    ('fs-on-open', 'filesystem', 'On-Open Tail', @Config, 'Healthy', @AddedUtc, 1)
                """,
                new
                {
                    Config = $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""",
                    AddedUtc = DateTime.UtcNow.ToString("O"),
                });
        }
        SqliteConnection.ClearAllPools();

        // Reopen the volume WITHOUT passing an explicit ProviderRegistry — should default to
        // BrainBackedProviderRegistry and pick up the row we just inserted.
        var openOptions = new VolumeCreationOptions
        {
            LoggerFactory = NullLoggerFactory.Instance,
            NotificationBus = new RecordingNotificationBus(),
        };
        var openResult = await FlashSkinkVolume.OpenAsync(skinkRoot, password, openOptions);
        Assert.True(openResult.Success);

        try
        {
            // Reach in and read the registry field via reflection — the public API surface for
            // listing tails arrives in §4.6 (ListTailsAsync).
            var registryField = typeof(FlashSkinkVolume).GetField(
                "_providerRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var registry = (IProviderRegistry)registryField.GetValue(openResult.Value!)!;
            Assert.IsType<BrainBackedProviderRegistry>(registry);

            var ids = (await registry.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
            Assert.Single(ids);
            Assert.Equal("fs-on-open", ids[0]);
        }
        finally
        {
            await openResult.Value!.DisposeAsync();
        }
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────

    private sealed class DisposableFakeProvider(string id) : IStorageProvider, IAsyncDisposable
    {
        public int DisposeCallCount { get; private set; }
        public bool Disposed => DisposeCallCount > 0;

        public string ProviderID { get; } = id;
        public string ProviderType => "fake";
        public string DisplayName { get; } = "Fake";

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }

        public Task<Result<UploadSession>> BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result> UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result> DeleteAsync(string remoteId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<long>> GetUsedBytesAsync(CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Minimal in-memory logger factory that records every emitted log line for assertion.
    /// Mirrors the pattern in <c>FlashSkinkVolumeUploadIntegrationTests</c>.
    /// </summary>
    private sealed class ListLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _entries = [];
        private readonly Lock _lock = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public string Dump()
        {
            lock (_lock) { return string.Join(" || ", _entries); }
        }

        private void Record(string entry)
        {
            lock (_lock) { _entries.Add(entry); }
        }

        private sealed class ListLogger(ListLoggerFactory parent, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, exception);
                parent.Record($"[{logLevel}] {category}: {msg}");
            }
        }
    }
}
