using System.ComponentModel;
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
