using FlashSkink.CLI;
using FlashSkink.CLI.Setup;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace FlashSkink.CLI;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Wire Serilog as the MEL provider. Logs go to a compact JSON file on the skink itself;
        // stdout is reserved for command output (Principle 7 — no host state, Principle 26 —
        // secrets never logged). The log path is written to a temp location here because the
        // skink root is not known until the command parses it; a future phase wires the log to
        // {skinkRoot}/.flashskink/logs/skink.log instead.
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(Path.GetTempPath(), "flashskink-cli-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .Enrich.FromLogContext()
            .CreateLogger();

        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);

        await using var factory = CliSetupFactory.CreateProduction(loggerFactory);

        // RootCommand displayed name defaults to the executable filename, which is already
        // "skink" (via <AssemblyName>skink</AssemblyName>). No explicit name override needed.
        return await factory.RootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
    }
}
