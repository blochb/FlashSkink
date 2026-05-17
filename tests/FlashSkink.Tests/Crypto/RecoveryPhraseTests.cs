using System.Reflection;
using FlashSkink.Core.Abstractions.Crypto;
using FlashSkink.Core.Abstractions.Results;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class RecoveryPhraseTests
{
    [Fact]
    public void FromUserInput_NullList_ReturnsInvalidMnemonic()
    {
        var result = RecoveryPhrase.FromUserInput(null!);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void FromUserInput_NullWord_ReturnsInvalidMnemonic()
    {
        var words = new string[] { "alpha", null!, "gamma" };

        var result = RecoveryPhrase.FromUserInput(words);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void FromUserInput_ValidWords_ReturnsPhraseWithMatchingContent()
    {
        var words = new[] { "alpha", "bravo", "charlie" };

        using var phrase = RecoveryPhrase.FromUserInput(words).Value!;

        Assert.Equal(3, phrase.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(phrase[i].SequenceEqual(words[i]));
        }
    }

    [Fact]
    public void Dispose_ZeroesUnderlyingBuffers()
    {
        // White-box test: reach into the private _words field via reflection,
        // hold the references, dispose, then assert every char[] is all-zero.
        // This is the only way to verify the security-critical zeroization
        // claim isn't a no-op (CLAUDE.md Principle 31).
        var words = new[] { "alpha", "bravo", "charlie" };
        var phrase = RecoveryPhrase.FromUserInput(words).Value!;

        var field = typeof(RecoveryPhrase).GetField("_words",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var buffers = (char[][])field.GetValue(phrase)!;
        // Sanity check: buffers initially contain the input chars.
        Assert.True(buffers[0].SequenceEqual("alpha".ToCharArray()));

        phrase.Dispose();

        foreach (var buf in buffers)
        {
            Assert.All(buf, c => Assert.Equal('\0', c));
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var phrase = RecoveryPhrase.FromUserInput(new[] { "a", "b" }).Value!;

        phrase.Dispose();
        // Second dispose must not throw.
        phrase.Dispose();
    }

    [Fact]
    public void Count_AfterDispose_ThrowsObjectDisposed()
    {
        var phrase = RecoveryPhrase.FromUserInput(new[] { "a", "b" }).Value!;
        phrase.Dispose();

        Assert.Throws<ObjectDisposedException>(() => phrase.Count);
    }

    [Fact]
    public void Indexer_AfterDispose_ThrowsObjectDisposed()
    {
        var phrase = RecoveryPhrase.FromUserInput(new[] { "a", "b" }).Value!;
        phrase.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            // Force evaluation of the indexer; ReadOnlySpan<char> is a ref struct
            // so it must be consumed inside the lambda (no capture).
            _ = phrase[0].Length;
        });
    }
}
