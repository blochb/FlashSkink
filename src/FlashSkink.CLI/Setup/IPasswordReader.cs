namespace FlashSkink.CLI.Setup;

/// <summary>
/// Seam for reading the volume password during setup commands. Injected by tests to avoid
/// real TTY interaction. The production implementation is <see cref="ConsolePasswordReader"/>.
/// </summary>
public interface IPasswordReader
{
    /// <summary>
    /// Reads the volume password, displaying <paramref name="prompt"/> to the user first.
    /// Never returns <see langword="null"/>; returns an empty string when no input is available.
    /// </summary>
    string ReadPassword(string prompt);
}
