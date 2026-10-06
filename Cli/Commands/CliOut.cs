using ForexTradingBot.Cli.Secrets;
using Spectre.Console;

namespace ForexTradingBot.Cli.Commands;

/// <summary>
/// Common colour/output helpers so every command prints the same way, and so
/// a test can capture the output instead of reading the console.
/// </summary>
internal static class CliOut
{
    public static void Info(string message) => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(message)}[/]");

    public static void Ok(string message) => AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");

    public static void Warn(string message) => AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");

    public static void Error(string message) => AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(message)}");

    /// <summary>The banner shown by every invocation (or by <c>forexbot</c> with no arguments).</summary>
    public static void Banner()
    {
        AnsiConsole.Write(new FigletText("ForexTradingBot").Centered().Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]Advanced AI-driven Forex signals bot — local command line[/]");
        AnsiConsole.WriteLine();
    }
}
