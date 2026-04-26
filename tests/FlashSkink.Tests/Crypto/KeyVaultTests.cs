using System.Security.Cryptography;
using System.Text;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class KeyVaultTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KeyVault _sut;
    private readonly ReadOnlyMemory<byte> _password;

    public KeyVaultTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KeyVaultTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new KeyVault(new KeyDerivationService(), new MnemonicService());
        _password = Encoding.UTF8.GetBytes("correct-horse-battery-staple");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string VaultPath() => Path.Combine(_tempDir, "vault.bin");

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WritesFileOfExactly100Bytes()
    {
        var result = await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(100, new FileInfo(VaultPath()).Length);
    }

    [Fact]
    public async Task CreateAsync_ReturnsDekOf32Bytes()
    {
        var result = await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(32, result.Value!.Length);
    }

    [Fact]
    public async Task CreateAsync_ReturnsDekThatIsNonZero()
    {
        var result = await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value!.All(b => b == 0));
    }

    [Fact]
    public async Task CreateAsync_TwoCalls_ProduceDifferentDeks()
    {
        var path2 = VaultPath() + ".2";

        var r1 = await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        var r2 = await _sut.CreateAsync(path2, _password, CancellationToken.None);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.False(r1.Value!.SequenceEqual(r2.Value!));
    }

    [Fact]
    public async Task CreateAsync_WhenCancelled_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.CreateAsync(VaultPath(), _password, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── VaultFile magic / version ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_VaultFile_StartsWithFsvtMagic()
    {
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(VaultPath());
        Assert.Equal((byte)'F', bytes[0]);
        Assert.Equal((byte)'S', bytes[1]);
        Assert.Equal((byte)'V', bytes[2]);
        Assert.Equal((byte)'T', bytes[3]);
    }

    [Fact]
    public async Task CreateAsync_VaultFile_HasVersion1()
    {
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(VaultPath());
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        Assert.Equal((ushort)1, version);
    }

    [Fact]
    public async Task CreateAsync_VaultFile_MemoryParamRoundTripsExactly()
    {
        // 19456 KB → byte[6] = 19 (MiB), decode = 19 * 1024 = 19456 KB — no precision loss.
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(VaultPath());
        var memMib = bytes[6];
        var memKilobytes = memMib * 1024;
        Assert.Equal(19456, memKilobytes);
    }

    // ── UnlockAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockAsync_WithCorrectPassword_ReturnsOriginalDek()
    {
        var createResult = await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        var expectedDek = createResult.Value!;

        var unlockResult = await _sut.UnlockAsync(VaultPath(), _password, CancellationToken.None);

        Assert.True(unlockResult.Success);
        Assert.True(expectedDek.SequenceEqual(unlockResult.Value!));
    }

    [Fact]
    public async Task UnlockAsync_WithWrongPassword_ReturnsInvalidPassword()
    {
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        ReadOnlyMemory<byte> wrong = Encoding.UTF8.GetBytes("wrong-password");

        var result = await _sut.UnlockAsync(VaultPath(), wrong, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidPassword, result.Error!.Code);
    }

    [Fact]
    public async Task UnlockAsync_VaultNotFound_ReturnsVolumeNotFound()
    {
        var result = await _sut.UnlockAsync(VaultPath(), _password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task UnlockAsync_CorruptMagic_ReturnsVolumeCorrupt()
    {
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(VaultPath());
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(VaultPath(), bytes);

        var result = await _sut.UnlockAsync(VaultPath(), _password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task UnlockAsync_WrongSize_ReturnsVolumeCorrupt()
    {
        await File.WriteAllBytesAsync(VaultPath(), new byte[50]);

        var result = await _sut.UnlockAsync(VaultPath(), _password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    // ── ChangePasswordAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ChangePasswordAsync_ThenUnlock_WithNewPassword_Succeeds()
    {
        var createResult = await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        var originalDek = createResult.Value!;
        ReadOnlyMemory<byte> newPassword = Encoding.UTF8.GetBytes("new-super-secret");

        var changeResult = await _sut.ChangePasswordAsync(VaultPath(), _password, newPassword, CancellationToken.None);

        Assert.True(changeResult.Success);

        var unlockResult = await _sut.UnlockAsync(VaultPath(), newPassword, CancellationToken.None);
        Assert.True(unlockResult.Success);
        Assert.True(originalDek.SequenceEqual(unlockResult.Value!));
    }

    [Fact]
    public async Task ChangePasswordAsync_WithWrongCurrentPassword_ReturnsInvalidPassword()
    {
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        ReadOnlyMemory<byte> wrong = Encoding.UTF8.GetBytes("not-the-right-one");
        ReadOnlyMemory<byte> newPassword = Encoding.UTF8.GetBytes("new-pass");

        var result = await _sut.ChangePasswordAsync(VaultPath(), wrong, newPassword, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidPassword, result.Error!.Code);
    }

    [Fact]
    public async Task ChangePasswordAsync_OldPasswordNoLongerWorks_After()
    {
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        ReadOnlyMemory<byte> newPassword = Encoding.UTF8.GetBytes("replacement");

        await _sut.ChangePasswordAsync(VaultPath(), _password, newPassword, CancellationToken.None);
        var result = await _sut.UnlockAsync(VaultPath(), _password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidPassword, result.Error!.Code);
    }

    // ── UnlockFromMnemonicAsync ───────────────────────────────────────────────

    [Fact(Skip = "deferred to §1.6 — requires CreateAsync to accept a mnemonic-derived KEK")]
    public Task UnlockFromMnemonicAsync_RoundTrip_Succeeds() => Task.CompletedTask;

    // ── Stored KDF params are consumed on unlock ──────────────────────────────

    [Fact]
    public async Task UnlockAsync_PatchedMemoryParam_FailsWithInvalidPassword()
    {
        // Prove the stored MemoryMib is read and fed to Argon2id on unlock, not silently ignored.
        // Strategy: create a valid vault, then corrupt byte[6] (MemoryMib) to a wrong value.
        // If UnlockAsync uses the stored param it will derive a different KEK → tag mismatch.
        // If it ignores the stored param (using the hardcoded constant) it would succeed — that's the bug.
        await _sut.CreateAsync(VaultPath(), _password, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(VaultPath());
        bytes[6] = (byte)(bytes[6] + 1); // corrupt MemoryMib
        await File.WriteAllBytesAsync(VaultPath(), bytes);

        var result = await _sut.UnlockAsync(VaultPath(), _password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidPassword, result.Error!.Code);
    }
}
