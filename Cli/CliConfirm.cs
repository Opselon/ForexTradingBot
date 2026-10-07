using Spectre.Console;

namespace ForexTradingBot.Cli;

/// <summary>
/// Confirmation prompts that behave correctly when stdin is redirected.
/// <see cref="AnsiConsole.Confirm(string, bool)"/> throws a Spectre exception under a
/// piped stdin (CI, scripts, tests), which used to surface as exit code 255 with a
/// framework stack trace. This helper uses the interactive prompt on a real TTY and
/// falls back to reading one plain line from stdin otherwise, so <c>echo y | …</c>
/// works while an empty pipe still fails closed instead of crashing.
/// </summary>
internal static class CliConfirm
{
    /// <summary>
    /// Asks the question and returns the answer in <paramref name="confirmed"/>.
    /// Returns <c>true</c> when an answer was obtained, <c>false</c> when stdin was
    /// redirected but held no readable answer (fail closed).
    /// </summary>
    public static bool TryAsk(string markup, out bool confirmed, bool defaultValue = false)
    {
        if (!Console.IsInputRedirected)
        {
            confirmed = AnsiConsole.Confirm(markup, defaultValue);
            return true;
        }

        // Headless: mimic the "[y/n] (n):" prompt shape, then read lines until the
        // user answers something recognisable. An unrecognised answer re-prompts,
        // mirroring what the interactive prompt does with a TTY.
        var fallbackDefault = defaultValue ? 'y' : 'n';
        try
        {
            while (true)
            {
                AnsiConsole.WriteLine($"{Markup.Remove(markup)} [y/n] ({fallbackDefault}):");

                var line = Console.In.ReadLine();
                if (line is null)
                {
                    // End of stream with no usable answer: do not proceed.
                    confirmed = defaultValue;
                    return false;
                }

                var answer = line.Trim().ToLowerInvariant();
                if (answer is "y" or "yes")
                {
                    confirmed = true;
                    return true;
                }

                if (answer is "n" or "no")
                {
                    confirmed = false;
                    return true;
                }

                if (answer.Length is 0)
                {
                    confirmed = defaultValue;
                    return true;
                }

                // Anything else is not an answer; ask again.
            }
        }
        catch
        {
            // No answer available at all: do not proceed with a destructive action.
            confirmed = defaultValue;
            return false;
        }
    }
}
