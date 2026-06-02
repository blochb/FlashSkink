using FlashSkink.CLI.Setup;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Test double for <see cref="IPasswordReader"/>. Returns a fixed password string without
/// touching the TTY or stdin.
/// </summary>
internal sealed class FakePasswordReader : IPasswordReader
{
    private readonly string _password;

    /// <summary>Creates a reader that always returns <paramref name="password"/>.</summary>
    public FakePasswordReader(string password) => _password = password;

    /// <inheritdoc/>
    public string ReadPassword(string prompt) => _password;
}
