namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Resolves the repository root by walking up from <see cref="AppContext.BaseDirectory"/>
/// until <c>FlashSkink.sln</c> is found. Used by source-grep tests that read production files.
/// </summary>
internal static class RepoRoot
{
    private static readonly string s_path = FindRepoRoot();

    /// <summary>The absolute path of the repository root directory.</summary>
    internal static string Path => s_path;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // Support both the legacy .sln format and the modern .slnx format.
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "FlashSkink.slnx")) ||
                File.Exists(System.IO.Path.Combine(dir.FullName, "FlashSkink.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Cannot find FlashSkink.slnx or FlashSkink.sln walking up from {AppContext.BaseDirectory}. " +
            "Ensure the test is run from within the repository tree.");
    }
}
