using System.CommandLine;
using FlashSkink.CLI.Setup;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace FlashSkink.CLI.Commands.Setup;

/// <summary>
/// <c>skink setup remove --provider &lt;id&gt;</c> — removes a tail from the skink volume.
/// </summary>
/// <remarks>
/// Removing a tail deletes the <c>Providers</c>, <c>TailUploads</c>, and <c>UploadSessions</c>
/// rows from the brain. Remote data is NOT deleted — the user is reminded of this in the
/// success message (Principle 25: user vocabulary).
/// </remarks>
public sealed class SetupRemoveCommand
{
    private readonly IPasswordReader _passwordReader;
    private readonly INotificationBus _notificationBus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<string, bool> _confirmPrompt;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    /// <param name="passwordReader">Seam for volume-password resolution.</param>
    /// <param name="notificationBus">Bus passed to <see cref="VolumeCreationOptions"/>.</param>
    /// <param name="loggerFactory">Logger factory passed to <see cref="VolumeCreationOptions"/>.</param>
    /// <param name="confirmPrompt">Optional seam for the yes/no confirmation prompt.
    /// Defaults to a <see cref="Console.ReadLine"/> prompt. Injected in tests.</param>
    /// <param name="output">Where to write success messages. Defaults to <see cref="Console.Out"/>.</param>
    /// <param name="error">Where to write error messages. Defaults to <see cref="Console.Error"/>.</param>
    public SetupRemoveCommand(
        IPasswordReader passwordReader,
        INotificationBus notificationBus,
        ILoggerFactory loggerFactory,
        Func<string, bool>? confirmPrompt = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        _passwordReader = passwordReader;
        _notificationBus = notificationBus;
        _loggerFactory = loggerFactory;
        _confirmPrompt = confirmPrompt ?? DefaultConfirmPrompt;
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
    }

    /// <summary>Returns the configured <see cref="Command"/> for wiring into the <c>setup</c> parent.</summary>
    public Command Build()
    {
        var skinkOption = new Option<string>("--skink")
        { Description = "Path to the skink root directory.", Required = true };

        var providerOption = new Option<string>("--provider")
        { Description = "Provider ID of the tail to remove.", Required = true };

        var passwordOption = new Option<string?>("--password")
        { Description = "Volume password. Prefer piped stdin or interactive prompt." };

        var yesOption = new Option<bool>("--yes")
        { Description = "Skip the confirmation prompt." };

        var forceOption = new Option<bool>("--force")
        { Description = "Force-unlock a stale single-instance lock." };

        var cmd = new Command("remove", "Remove a tail from the skink.")
        {
            skinkOption,
            providerOption,
            passwordOption,
            yesOption,
            forceOption,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var skinkPath = pr.GetRequiredValue(skinkOption);
            var providerId = pr.GetRequiredValue(providerOption);
            var password = pr.GetValue(passwordOption);
            var skipYes = pr.GetValue(yesOption);
            var forceUnlock = pr.GetValue(forceOption);

            if (!skipYes)
            {
                var confirmed = _confirmPrompt(
                    $"Remove tail '{providerId}' from skink? " +
                    "Remote data will NOT be deleted. Type 'yes' to confirm: ");
                if (!confirmed)
                {
                    await _output.WriteLineAsync("Cancelled.").ConfigureAwait(false);
                    return 0;
                }
            }

            var resolvedPassword = password
                ?? _passwordReader.ReadPassword("Volume password: ");

            var volumeOptions = new VolumeCreationOptions
            {
                LoggerFactory = _loggerFactory,
                NotificationBus = _notificationBus,
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

            var removeResult = await volume.RemoveTailAsync(providerId, ct).ConfigureAwait(false);

            if (!removeResult.Success)
            {
                await _error.WriteLineAsync(
                    $"Error: Could not remove tail: {removeResult.Error!.Message}")
                    .ConfigureAwait(false);
                return 1;
            }

            // Principle 25: user vocabulary; remind user that remote data is untouched.
            await _output.WriteLineAsync("✓ Tail removed from skink.").ConfigureAwait(false);
            await _output.WriteLineAsync(
                $"Your data in '{providerId}' is not deleted — remove it there yourself if you want.")
                .ConfigureAwait(false);
            return 0;
        });

        return cmd;
    }

    private static bool DefaultConfirmPrompt(string message)
    {
        Console.Write(message);
        var answer = Console.ReadLine()?.Trim() ?? string.Empty;
        return string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase);
    }
}
