using System.CommandLine;
using FlashSkink.CLI.Setup;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace FlashSkink.CLI.Commands.Setup;

/// <summary>
/// <c>skink setup add</c> — adds a new tail to an open skink volume.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Filesystem tails</strong> (no OAuth): validates the root path via the volume's
/// built-in FileSystem setup and registers the tail. Requires <c>--root</c>.
/// </para>
/// <para>
/// <strong>Cloud tails</strong> (OAuth): runs the RFC 8252 loopback dance in-process, passing
/// the auth code and BYOC credentials to <c>AddTailAsync</c>, which performs the token exchange
/// and encrypted-token persistence with access to the DEK (Principles 6 and 26 — credentials
/// never echo to stdout, log, or <c>ErrorContext</c>).
/// </para>
/// </remarks>
public sealed class SetupAddCommand
{
    private readonly IReadOnlyList<IProviderSetup> _cloudSetups;
    private readonly IOAuthCaptureFlow _oauthCapture;
    private readonly IPasswordReader _passwordReader;
    private readonly INotificationBus _notificationBus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    /// <param name="cloudSetups">Injected cloud provider setups (Google Drive, Dropbox, OneDrive).
    /// Filesystem is handled by the volume internally and is not in this list.</param>
    /// <param name="oauthCapture">OAuth capture flow for the in-browser sign-in dance.</param>
    /// <param name="passwordReader">Seam for volume-password resolution.</param>
    /// <param name="notificationBus">Bus passed to <see cref="VolumeCreationOptions"/>.</param>
    /// <param name="loggerFactory">Logger factory passed to <see cref="VolumeCreationOptions"/>.</param>
    /// <param name="output">Where to write success messages. Defaults to <see cref="Console.Out"/>.</param>
    /// <param name="error">Where to write error messages. Defaults to <see cref="Console.Error"/>.</param>
    public SetupAddCommand(
        IReadOnlyList<IProviderSetup> cloudSetups,
        IOAuthCaptureFlow oauthCapture,
        IPasswordReader passwordReader,
        INotificationBus notificationBus,
        ILoggerFactory loggerFactory,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        _cloudSetups = cloudSetups;
        _oauthCapture = oauthCapture;
        _passwordReader = passwordReader;
        _notificationBus = notificationBus;
        _loggerFactory = loggerFactory;
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
    }

    /// <summary>Returns the configured <see cref="Command"/> for wiring into the <c>setup</c> parent.</summary>
    public Command Build()
    {
        var skinkOption = new Option<string>("--skink")
        { Description = "Path to the skink root directory.", Required = true };

        var providerOption = new Option<string>("--provider")
        { Description = "Provider type: filesystem, google-drive, dropbox, or onedrive.", Required = true };

        var clientIdOption = new Option<string?>("--client-id")
        { Description = "OAuth client ID (required for cloud providers)." };

        var clientSecretOption = new Option<string?>("--client-secret")
        { Description = "OAuth client secret (required for cloud providers). Never echoed." };

        var rootOption = new Option<string?>("--root")
        { Description = "Local folder path (required for filesystem provider)." };

        var passwordOption = new Option<string?>("--password")
        { Description = "Volume password. Prefer piped stdin or interactive prompt." };

        var forceOption = new Option<bool>("--force")
        { Description = "Force-unlock a stale single-instance lock (use with caution)." };

        var cmd = new Command("add", "Add a new tail to the skink.")
        {
            skinkOption,
            providerOption,
            clientIdOption,
            clientSecretOption,
            rootOption,
            passwordOption,
            forceOption,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var skinkPath = pr.GetRequiredValue(skinkOption);
            var providerName = pr.GetRequiredValue(providerOption);
            var clientId = pr.GetValue(clientIdOption);
            var clientSecret = pr.GetValue(clientSecretOption);
            var rootPath = pr.GetValue(rootOption);
            var password = pr.GetValue(passwordOption);
            var forceUnlock = pr.GetValue(forceOption);

            // Resolution order: --password arg → prompt (reader handles redirected stdin).
            var resolvedPassword = password
                ?? _passwordReader.ReadPassword("Volume password: ");

            // Build TailConfiguration — filesystem path or OAuth.
            TailConfiguration config;
            var isFilesystem = string.Equals(
                providerName, "filesystem", StringComparison.OrdinalIgnoreCase);

            if (isFilesystem)
            {
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    await _error.WriteLineAsync(
                        "Error: --root is required for the filesystem provider. " +
                        $"Run `{CliMetadata.CommandName} setup guide --provider filesystem` for help.")
                        .ConfigureAwait(false);
                    return 1;
                }

                config = new TailConfiguration
                {
                    ProviderType = "filesystem",
                    LocalPath = rootPath,
                };
            }
            else
            {
                // Locate the matching cloud setup for the OAuth dance.
                var setup = _cloudSetups.FirstOrDefault(
                    s => string.Equals(s.ProviderType, providerName, StringComparison.OrdinalIgnoreCase));

                if (setup is null)
                {
                    var known = string.Join(", ",
                        _cloudSetups.Select(s => s.ProviderType).Prepend("filesystem"));
                    await _error.WriteLineAsync(
                        $"Error: Unknown provider '{providerName}'. Supported: {known}. " +
                        $"Run `{CliMetadata.CommandName} setup guide --provider <name>` for setup instructions.")
                        .ConfigureAwait(false);
                    return 1;
                }

                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                {
                    await _error.WriteLineAsync(
                        $"Error: --client-id and --client-secret are required for {setup.DisplayName}. " +
                        $"Run `{CliMetadata.CommandName} setup guide --provider {providerName}` for instructions.")
                        .ConfigureAwait(false);
                    return 1;
                }

                // RFC 8252 loopback OAuth dance.
                var prepareResult = _oauthCapture.Prepare();
                if (!prepareResult.Success)
                {
                    await _error.WriteLineAsync(
                        $"Error: Could not start the sign-in flow: {prepareResult.Error!.Message}")
                        .ConfigureAwait(false);
                    return 1;
                }

                var ctx = prepareResult.Value!; // Success ↔ Value: invariant held by Result<T>.
                // Principle 26: clientSecret NOT passed to log template.
                var credentials = new ProviderCredentials { ClientId = clientId, ClientSecret = clientSecret };

                var uriResult = await setup.GetAuthorizationUriAsync(
                    ctx.RedirectUri, ctx.CodeChallenge, credentials, ct)
                    .ConfigureAwait(false);
                if (!uriResult.Success)
                {
                    await _error.WriteLineAsync(
                        $"Error: Could not build the sign-in URL: {uriResult.Error!.Message}")
                        .ConfigureAwait(false);
                    return 1;
                }

                var authUri = uriResult.Value!; // Success ↔ Value.
                await _output.WriteLineAsync(
                    $"Opening your browser to sign in to {setup.DisplayName}.")
                    .ConfigureAwait(false);
                await _output.WriteLineAsync(
                    "If the browser does not open automatically, visit:")
                    .ConfigureAwait(false);
                await _output.WriteLineAsync($"  {authUri}").ConfigureAwait(false);

                var codeResult = await _oauthCapture.AwaitAuthorizationCodeAsync(ctx, authUri, ct)
                    .ConfigureAwait(false);
                if (!codeResult.Success)
                {
                    await _error.WriteLineAsync(
                        $"Error: Sign-in did not complete: {codeResult.Error!.Message}")
                        .ConfigureAwait(false);
                    return 1;
                }

                // Principle 26: auth code and client secret never appear in logs or output.
                config = new TailConfiguration
                {
                    ProviderType = providerName,
                    ClientId = clientId,
                    ClientSecret = clientSecret,   // zeroed inside AddTailAsync
                    AuthorizationCode = codeResult.Value,
                    CodeVerifier = ctx.CodeVerifier,
                    RedirectUri = ctx.RedirectUri,
                };
            }

            var volumeOptions = new VolumeCreationOptions
            {
                LoggerFactory = _loggerFactory,
                NotificationBus = _notificationBus,
                ProviderSetups = _cloudSetups,
                ForceUnlock = forceUnlock,
            };

            var openResult = await FlashSkinkVolume.OpenAsync(
                skinkPath, resolvedPassword, volumeOptions, ct)
                .ConfigureAwait(false);

            if (!openResult.Success)
            {
                await _error.WriteLineAsync(
                    $"Error: Could not open skink at '{skinkPath}': {openResult.Error!.Message}")
                    .ConfigureAwait(false);
                return 1;
            }

            await using var volume = openResult.Value!; // Success ↔ Value.

            var addResult = await volume.AddTailAsync(config, ct).ConfigureAwait(false);

            if (!addResult.Success)
            {
                await _error.WriteLineAsync(
                    $"Error: Could not configure tail: {addResult.Error!.Message}")
                    .ConfigureAwait(false);
                return 1;
            }

            // Principle 25: user vocabulary ("tail", not "provider").
            await _output.WriteLineAsync(
                $"✓ {addResult.Value!.DisplayName} tail configured.")
                .ConfigureAwait(false);
            return 0;
        });

        return cmd;
    }
}
