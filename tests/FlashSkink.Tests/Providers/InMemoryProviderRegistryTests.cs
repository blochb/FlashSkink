using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers;

public sealed class InMemoryProviderRegistryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly InMemoryProviderRegistry _sut;

    public InMemoryProviderRegistryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _sut = new InMemoryProviderRegistry(NullLogger<InMemoryProviderRegistry>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private FileSystemProvider MakeProvider(string id = "p1")
    {
        var root = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(root);
        return new FileSystemProvider(id, $"Provider {id}", root, NullLogger<FileSystemProvider>.Instance);
    }

    [Fact]
    public async Task Register_NewProvider_GetReturnsIt()
    {
        var provider = MakeProvider("p1");
        _sut.Register("p1", provider);

        var result = await _sut.GetAsync("p1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Same(provider, result.Value);
    }

    [Fact]
    public async Task Register_OverwritesExistingId()
    {
        var first = MakeProvider("pa");
        var second = MakeProvider("pb");
        _sut.Register("p1", first);
        _sut.Register("p1", second);

        var result = await _sut.GetAsync("p1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Same(second, result.Value);
    }

    [Fact]
    public void Remove_ReturnsTrueForKnown_FalseForUnknown()
    {
        _sut.Register("p1", MakeProvider("p1"));

        Assert.True(_sut.Remove("p1"));
        Assert.False(_sut.Remove("p1")); // already removed
        Assert.False(_sut.Remove("p-never-existed"));
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsProviderUnreachable()
    {
        var result = await _sut.GetAsync("not-registered", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public async Task ListActiveProviderIdsAsync_ReflectsRegisterAndRemove()
    {
        _sut.Register("p1", MakeProvider("p1"));
        _sut.Register("p2", MakeProvider("p2"));

        var list1 = (await _sut.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Equal(2, list1.Count);

        _sut.Remove("p1");
        var list2 = (await _sut.ListActiveProviderIdsAsync(CancellationToken.None)).Value!;
        Assert.Single(list2);
        Assert.Equal("p2", list2[0]);
    }

    [Fact]
    public async Task ListActiveProviderIdsAsync_EmptyRegistry_ReturnsEmptyList()
    {
        var result = await _sut.ListActiveProviderIdsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ConcurrentRegisterRemove_NoTorn()
    {
        // 100 tasks: half register, half remove. Assert registry is coherent after all complete.
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var id = $"p{i}";
            var root = Path.Combine(_tempRoot, id);
            Directory.CreateDirectory(root);
            var provider = new FileSystemProvider(id, id, root, NullLogger<FileSystemProvider>.Instance);
            tasks.Add(Task.Run(() => _sut.Register(id, provider)));
            tasks.Add(Task.Run(() => _sut.Remove(id)));
        }

        await Task.WhenAll(tasks);

        // No exception means no torn state. Just confirm we can still query.
        var listResult = await _sut.ListActiveProviderIdsAsync(CancellationToken.None);
        Assert.True(listResult.Success);
    }
}
