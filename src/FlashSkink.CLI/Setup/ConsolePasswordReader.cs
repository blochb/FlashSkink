using System.Text;

namespace FlashSkink.CLI.Setup;

/// <summary>
/// Production <see cref="IPasswordReader"/> with three-tier resolution:
/// <list type="number">
/// <item><description>If <c>Console.IsInputRedirected</c> is <see langword="true"/>, reads one
/// line from <c>Console.In</c> — <c>Console.ReadKey</c> throws when stdin is a pipe or
/// file.</description></item>
/// <item><description>Otherwise (interactive TTY), runs a masked <c>Console.ReadKey</c> loop
/// until Return, rendering each keystroke as <c>*</c> and honouring Backspace.</description></item>
/// </list>
/// The <c>--password</c> command-line option is the fully scriptable path; callers check it first
/// and only call this reader when the option was not supplied.
/// </summary>
public sealed class ConsolePasswordReader : IPasswordReader
{
    /// <inheritdoc/>
    public string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            // Stdin is a pipe or file — Console.ReadKey throws InvalidOperationException.
            return Console.In.ReadLine() ?? string.Empty;
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    // Erase the last '*' from the terminal.
                    Console.Write("\b \b");
                }
            }
            else if (key.KeyChar != '\0')
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return sb.ToString();
    }
}
