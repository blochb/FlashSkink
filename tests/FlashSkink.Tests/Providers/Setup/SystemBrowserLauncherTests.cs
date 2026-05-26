using System.ComponentModel;
using System.Runtime.InteropServices;
using FlashSkink.Core.Providers.Setup;
using Xunit;

namespace FlashSkink.Tests.Providers.Setup;

/// <summary>
/// Smoke test for <see cref="SystemBrowserLauncher"/>. The launcher delegates to OS-specific
/// processes (<c>xdg-open</c> / <c>open</c> / shell-execute); environmental failures on
/// headless CI runners (missing helper binary, unsupported platform) are accepted, but any
/// other exception is a real defect.
/// </summary>
public sealed class SystemBrowserLauncherTests
{
    [Fact]
    public void Launch_OnRunningPlatform_DoesNotThrow_OrThrowsExpectedEnvironmentException()
    {
        // Skip on Windows: ShellExecute on an unregistered URI scheme (such as "about:")
        // pops the "How do you want to open this?" Microsoft Store dialog, which is a
        // visible side effect on an interactive desktop and useless as a CI signal. The
        // Linux/macOS legs exercise the same branching logic without that hazard.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        var launcher = new SystemBrowserLauncher();

        var ex = Record.Exception(() => launcher.Launch(new Uri("about:blank")));

        // null → launcher accepted the URI. Win32Exception → e.g. xdg-open missing on headless
        // Linux. PlatformNotSupportedException → the launcher's own guard fired on an
        // unsupported OS. Anything else is a real defect.
        Assert.True(
            ex is null or Win32Exception or PlatformNotSupportedException,
            $"Unexpected exception from SystemBrowserLauncher.Launch: {ex}");
    }
}
