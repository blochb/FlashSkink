namespace FlashSkink.CLI;

/// <summary>
/// Compile-time constants for the CLI entry point. Centralises the user-facing command name so
/// renaming the binary is a single-constant change: update <see cref="CommandName"/> here and
/// <c>&lt;AssemblyName&gt;</c> in the project file and the change propagates to every help text,
/// usage example, and embedded setup guide.
/// </summary>
public static class CliMetadata
{
    /// <summary>
    /// User-facing command name. Matches <c>&lt;AssemblyName&gt;skink&lt;/AssemblyName&gt;</c>
    /// in <c>FlashSkink.CLI.csproj</c>. Used in help output, setup guides, and printed messages.
    /// </summary>
    public const string CommandName = "skink";

    /// <summary>One-line description shown in <c>--help</c> output.</summary>
    public const string Description = "FlashSkink — portable nomadic backup.";
}
