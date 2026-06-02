using System.CommandLine;
using FlashSkink.CLI.Commands.Setup;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Notifications;
using FlashSkink.Core.Providers.Dropbox;
using FlashSkink.Core.Providers.GoogleDrive;
using FlashSkink.Core.Providers.OneDrive;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging;

namespace FlashSkink.CLI.Setup;

/// <summary>
/// Composition root for the <c>setup</c> command subtree. Owns the lifetimes of the cloud
/// provider setup instances, the <see cref="LoopbackOAuthCapture"/>, and (in the production
/// path) the <see cref="NotificationBus"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Production path.</strong> Call <see cref="CreateProduction"/> from <c>Program.cs</c>
/// and dispose the returned <see cref="CliSetupFactory"/> in a <see langword="using"/> block so
/// cloud SDK clients and the loopback listener are torn down after the command completes.
/// </para>
/// <para>
/// <strong>Test path.</strong> Call <see cref="CreateForTest"/> and inject fakes for all
/// network-touching dependencies. The returned factory does NOT dispose the injected setups
/// (caller owns them).
/// </para>
/// </remarks>
public sealed class CliSetupFactory : IAsyncDisposable
{
    private readonly IReadOnlyList<IProviderSetup> _cloudSetups;
    private readonly IOAuthCaptureFlow _oauthCapture;
    private readonly INotificationBus _notificationBus;
    private readonly bool _ownsDisposables;

    /// <summary>The assembled root command, ready for <c>root.Parse(args).InvokeAsync()</c>.</summary>
    public RootCommand RootCommand { get; }

    private CliSetupFactory(
        RootCommand rootCommand,
        IReadOnlyList<IProviderSetup> cloudSetups,
        IOAuthCaptureFlow oauthCapture,
        INotificationBus notificationBus,
        bool ownsDisposables)
    {
        RootCommand = rootCommand;
        _cloudSetups = cloudSetups;
        _oauthCapture = oauthCapture;
        _notificationBus = notificationBus;
        _ownsDisposables = ownsDisposables;
    }

    /// <summary>
    /// Builds the production command tree with real cloud SDK clients, a loopback OAuth
    /// listener, and a concrete <see cref="NotificationBus"/>. Dispose the returned factory
    /// (or use <see langword="await using"/>) after <c>InvokeAsync</c> returns.
    /// </summary>
    public static CliSetupFactory CreateProduction(ILoggerFactory loggerFactory)
    {
        var googleSetup = new GoogleDriveSetup(loggerFactory);
        var dropboxSetup = new DropboxSetup(loggerFactory);
        var oneDriveSetup = new OneDriveSetup(loggerFactory);

        IReadOnlyList<IProviderSetup> cloudSetups = [googleSetup, dropboxSetup, oneDriveSetup];

        var oauthCapture = new LoopbackOAuthCapture(loggerFactory);

        var dispatcher = new NotificationDispatcher(
            loggerFactory.CreateLogger<NotificationDispatcher>());
        var bus = new NotificationBus(dispatcher, loggerFactory.CreateLogger<NotificationBus>());

        var passwordReader = new ConsolePasswordReader();

        var rootCommand = BuildRootCommand(
            cloudSetups, oauthCapture, passwordReader, bus, loggerFactory,
            output: null, error: null);

        return new CliSetupFactory(rootCommand, cloudSetups, oauthCapture, bus, ownsDisposables: true);
    }

    /// <summary>
    /// Builds a command tree with injected fakes for testing. The caller owns and disposes
    /// <paramref name="cloudSetups"/>, <paramref name="oauthCapture"/>, and
    /// <paramref name="notificationBus"/> — this factory does NOT dispose them on its own
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    /// <param name="confirmPrompt">
    /// Optional seam injected into the <c>remove</c> command's yes/no prompt. When
    /// <see langword="null"/>, the production <c>Console.ReadLine</c> prompt is used.
    /// Tests inject a deterministic <see cref="Func{T,TResult}"/> to avoid TTY interaction.
    /// </param>
    public static CliSetupFactory CreateForTest(
        ILoggerFactory loggerFactory,
        IReadOnlyList<IProviderSetup> cloudSetups,
        IOAuthCaptureFlow oauthCapture,
        IPasswordReader passwordReader,
        INotificationBus notificationBus,
        TextWriter output,
        TextWriter error,
        Func<string, bool>? confirmPrompt = null)
    {
        var rootCommand = BuildRootCommand(
            cloudSetups, oauthCapture, passwordReader, notificationBus, loggerFactory,
            output, error, confirmPrompt);

        return new CliSetupFactory(
            rootCommand, cloudSetups, oauthCapture, notificationBus, ownsDisposables: false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownsDisposables)
        {
            return;
        }

        // Dispose in reverse-creation order. Each disposal is isolated in its own
        // try/catch so one component's failure cannot orphan the others' unmanaged
        // resources (the OAuth listener's HttpListener, the cloud setups' HttpClients).
        // Principle 16: every failure path disposes partially-constructed resources.
        if (_notificationBus is IAsyncDisposable asyncBus)
        {
            try { await asyncBus.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort teardown at process exit */ }
        }

        if (_oauthCapture is IDisposable disposableCapture)
        {
            try { disposableCapture.Dispose(); }
            catch { /* best-effort teardown at process exit */ }
        }

        foreach (var setup in _cloudSetups)
        {
            if (setup is IDisposable d)
            {
                try { d.Dispose(); }
                catch { /* best-effort teardown at process exit */ }
            }
        }
    }

    private static RootCommand BuildRootCommand(
        IReadOnlyList<IProviderSetup> cloudSetups,
        IOAuthCaptureFlow oauthCapture,
        IPasswordReader passwordReader,
        INotificationBus notificationBus,
        ILoggerFactory loggerFactory,
        TextWriter? output,
        TextWriter? error,
        Func<string, bool>? confirmPrompt = null)
    {
        var rootCommand = new RootCommand(CliMetadata.Description);

        var setupCommand = new Command("setup", "Manage tails (add, remove, list).");

        setupCommand.Subcommands.Add(
            new SetupGuideCommand(output, error).Build());

        setupCommand.Subcommands.Add(
            new SetupAddCommand(cloudSetups, oauthCapture, passwordReader,
                notificationBus, loggerFactory, output, error).Build());

        setupCommand.Subcommands.Add(
            new SetupRemoveCommand(passwordReader, notificationBus, loggerFactory,
                confirmPrompt, output, error).Build());

        setupCommand.Subcommands.Add(
            new SetupListCommand(passwordReader, notificationBus, loggerFactory, output, error).Build());

        rootCommand.Subcommands.Add(setupCommand);

        return rootCommand;
    }
}
