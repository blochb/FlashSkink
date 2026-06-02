using System.CommandLine;
using FlashSkink.CLI.Setup;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace FlashSkink.CLI.Commands.Setup;

/// <summary>
/// <c>skink setup list</c> — lists all configured tails and their status.
/// </summary>
public sealed class SetupListCommand
{
    private readonly IPasswordReader _passwordReader;
    private readonly INotificationBus _notificationBus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    /// <param name="passwordReader">Seam for volume-password resolution.</param>
    /// <param name="notificationBus">Bus passed to <see cref="VolumeCreationOptions"/>.</param>
    /// <param name="loggerFactory">Logger factory passed to <see cref="VolumeCreationOptions"/>.</param>
    /// <param name="output">Where to write the table. Defaults to <see cref="Console.Out"/>.</param>
    /// <param name="error">Where to write error messages. Defaults to <see cref="Console.Error"/>.</param>
    public SetupListCommand(
        IPasswordReader passwordReader,
        INotificationBus notificationBus,
        ILoggerFactory loggerFactory,
        TextWriter? output = null,
        TextWriter? error = null)
    {
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

        var passwordOption = new Option<string?>("--password")
        { Description = "Volume password. Prefer piped stdin or interactive prompt." };

        var forceOption = new Option<bool>("--force")
        { Description = "Force-unlock a stale single-instance lock." };

        var cmd = new Command("list", "List all configured tails.")
        {
            skinkOption,
            passwordOption,
            forceOption,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var skinkPath = pr.GetRequiredValue(skinkOption);
            var password = pr.GetValue(passwordOption);
            var forceUnlock = pr.GetValue(forceOption);

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

            var listResult = await volume.ListTailsAsync(ct).ConfigureAwait(false);

            if (!listResult.Success)
            {
                await _error.WriteLineAsync(
                    $"Error: Could not list tails: {listResult.Error!.Message}")
                    .ConfigureAwait(false);
                return 1;
            }

            var tails = listResult.Value!; // Success ↔ Value.

            if (tails.Count == 0)
            {
                await _output.WriteLineAsync(
                    "No tails configured yet. " +
                    $"Run `{CliMetadata.CommandName} setup add` to add one.")
                    .ConfigureAwait(false);
                return 0;
            }

            // Simple fixed-width table.
            const int NameWidth = 22;
            const int HealthWidth = 10;
            const int CountWidth = 9;

            var header = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,-" + NameWidth + "} {1,-" + HealthWidth + "} {2," + CountWidth + "} {3," + CountWidth + "} {4}",
                "Display name", "Health", "Uploaded", "Pending", "Last upload");

            await _output.WriteLineAsync(header).ConfigureAwait(false);
            await _output.WriteLineAsync(new string('-', header.Length)).ConfigureAwait(false);

            foreach (var tail in tails)
            {
                var lastUpload = tail.LastSuccessfulUploadUtc?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
                var line = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0,-" + NameWidth + "} {1,-" + HealthWidth + "} {2," + CountWidth + "} {3," + CountWidth + "} {4}",
                    Truncate(tail.DisplayName, NameWidth),
                    tail.Health,
                    tail.UploadedFileCount,
                    tail.PendingFileCount,
                    lastUpload);

                await _output.WriteLineAsync(line).ConfigureAwait(false);
            }

            return 0;
        });

        return cmd;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
}
