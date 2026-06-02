using System.CommandLine;
using FlashSkink.CLI.Setup;

namespace FlashSkink.CLI.Commands.Setup;

/// <summary>
/// <c>skink setup guide --provider &lt;name&gt;</c> — prints the BYOC setup walkthrough for a
/// provider without opening a skink volume. The guide text is an embedded resource with
/// <c>{cli}</c> substituted to the binary name at read time.
/// </summary>
public sealed class SetupGuideCommand
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    /// <param name="output">Where to write guide text. Defaults to <see cref="Console.Out"/>.</param>
    /// <param name="error">Where to write error messages. Defaults to <see cref="Console.Error"/>.</param>
    public SetupGuideCommand(TextWriter? output = null, TextWriter? error = null)
    {
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
    }

    /// <summary>Returns the configured <see cref="Command"/> for wiring into the <c>setup</c> parent.</summary>
    public Command Build()
    {
        var providerOption = new Option<string>("--provider")
        {
            Description = "Provider name: google-drive, dropbox, onedrive, or filesystem.",
            Required = true,
        };

        var cmd = new Command("guide", "Print the BYOC setup guide for a provider.")
        {
            providerOption,
        };

        cmd.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            var providerName = pr.GetRequiredValue(providerOption);
            var result = SetupGuideResources.Read(providerName);

            if (!result.Success)
            {
                await _error.WriteLineAsync(result.Error!.Message).ConfigureAwait(false);
                return 1;
            }

            await _output.WriteAsync(result.Value).ConfigureAwait(false);
            return 0;
        });

        return cmd;
    }
}
