using System.Diagnostics;

namespace FlashSkink.Core.Providers.Setup;

/// <summary>
/// Production <see cref="IBrowserLauncher"/> — dispatches to the OS's default browser-launch
/// primitive. Sanctioned platform branching (cross-cutting decision 3 of phase-4-providers and
/// the Principle 12 carve-out): the launcher is the one place where OS differences in
/// "open this URL" surface; the surrounding <see cref="LoopbackOAuthCapture"/> listener is
/// identical on every OS.
/// </summary>
internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc/>
    public void Launch(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Process.Start returns a Process? owning a native handle; dispose immediately so the
        // handle is released deterministically rather than waiting on the finalizer
        // (Principle 16 — dispose partially-constructed resources).
        if (OperatingSystem.IsWindows())
        {
            // UseShellExecute=true asks the Windows shell to resolve the registered handler for
            // the URL scheme (typically the default browser).
            Process.Start(new ProcessStartInfo
            {
                FileName = url.ToString(),
                UseShellExecute = true,
            })?.Dispose();
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // `open <url>` on macOS launches the default browser.
            Process.Start("open", url.ToString())?.Dispose();
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            // `xdg-open <url>` is the freedesktop.org standard; available on every mainstream
            // distro (Ubuntu, Fedora, Arch, openSUSE, Debian). Headless CI runners without
            // xdg-open installed will surface Win32Exception — the caller maps that to
            // ErrorCode.Unknown.
            Process.Start("xdg-open", url.ToString())?.Dispose();
            return;
        }

        throw new PlatformNotSupportedException(
            $"Browser launch is not supported on platform '{Environment.OSVersion.Platform}'.");
    }
}
