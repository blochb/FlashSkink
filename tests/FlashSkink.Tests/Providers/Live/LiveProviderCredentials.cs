using FlashSkink.Core.Abstractions.Providers;

namespace FlashSkink.Tests.Providers.Live;

/// <summary>
/// Resolves BYOC OAuth credentials for the live provider tests from the process environment, with a
/// best-effort <c>.env</c> loader at the repo root. Credentials are <strong>never</strong> logged
/// (Principle 26). FlashSkink ships no shared credentials; these are the developer's own test-app
/// credentials, kept out of the repo per <c>CLAUDE.md</c> § "Secrets hygiene".
/// </summary>
internal static class LiveProviderCredentials
{
    private static readonly Lock _envLock = new();
    private static bool _envLoaded;
    private static string? _repoRoot;

    /// <summary>
    /// Returns the credentials for <paramref name="providerKey"/> (e.g. <c>"GDRIVE"</c>) and whether
    /// both <c>ClientId</c> and <c>ClientSecret</c> are present.
    /// </summary>
    public static (ProviderCredentials Credentials, bool Present) Resolve(string providerKey)
    {
        EnsureEnvLoaded();
        var key = providerKey.ToUpperInvariant();
        var clientId = Environment.GetEnvironmentVariable($"FLASHSKINK_LIVE_{key}_CLIENTID");
        var clientSecret = Environment.GetEnvironmentVariable($"FLASHSKINK_LIVE_{key}_CLIENTSECRET");
        var present = !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret);
        return (new ProviderCredentials { ClientId = clientId, ClientSecret = clientSecret }, present);
    }

    /// <summary>
    /// Absolute path to the repository root — the directory containing the FlashSkink solution file
    /// (<c>FlashSkink.slnx</c> or <c>FlashSkink.sln</c>), found by walking up from the test binary
    /// location. Falls back to the current directory.
    /// </summary>
    public static string RepoRoot()
    {
        if (_repoRoot is not null)
        {
            return _repoRoot;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlashSkink.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "FlashSkink.sln")))
            {
                _repoRoot = dir.FullName;
                return _repoRoot;
            }
            dir = dir.Parent;
        }

        _repoRoot = Directory.GetCurrentDirectory();
        return _repoRoot;
    }

    /// <summary>
    /// Loads <c>&lt;repoRoot&gt;/.env</c> into the process environment once (idempotent, thread-safe).
    /// Already-set process variables win over <c>.env</c> values (the shell takes precedence).
    /// Best-effort: a missing or malformed <c>.env</c> simply leaves variables unset, which makes
    /// the live tests skip.
    /// </summary>
    public static void EnsureEnvLoaded()
    {
        if (Volatile.Read(ref _envLoaded))
        {
            return;
        }

        lock (_envLock)
        {
            if (_envLoaded)
            {
                return;
            }

            try
            {
                var envPath = Path.Combine(RepoRoot(), ".env");
                if (File.Exists(envPath))
                {
                    foreach (var rawLine in File.ReadAllLines(envPath))
                    {
                        ApplyEnvLine(rawLine);
                    }
                }
            }
            catch
            {
                // Best-effort: .env loading failures just mean credentials stay unset → tests skip.
            }

            _envLoaded = true;
        }
    }

    private static void ApplyEnvLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            return;
        }

        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return;
        }

        var name = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        // Do not override a variable already set in the shell environment.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
