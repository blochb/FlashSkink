namespace FlashSkink.Core.Providers.Setup;

/// <summary>
/// Internal seam over the OS browser-launch primitive used by <see cref="LoopbackOAuthCapture"/>.
/// Production uses <see cref="SystemBrowserLauncher"/>; tests inject a recording double so the
/// integration test can drive the loopback flow end-to-end without spawning a real browser.
/// </summary>
internal interface IBrowserLauncher
{
    /// <summary>
    /// Opens <paramref name="url"/> in the user's default browser using the OS launcher.
    /// Non-blocking — returns as soon as the launcher process is started.
    /// </summary>
    /// <remarks>
    /// Failure to launch surfaces as <see cref="System.ComponentModel.Win32Exception"/> (missing
    /// helper binary such as <c>xdg-open</c> on a headless Linux runner),
    /// <see cref="System.IO.FileNotFoundException"/>, or
    /// <see cref="PlatformNotSupportedException"/> (the launcher's own guard).
    /// <see cref="LoopbackOAuthCapture.AwaitAuthorizationCodeAsync"/> catches and maps any
    /// exception to <see cref="FlashSkink.Core.Abstractions.Results.ErrorCode.Unknown"/>.
    /// </remarks>
    void Launch(Uri url);
}
