using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace ForexTradingBot.Cli.Commands;

/// <summary>
/// <c>forexbot doctor</c>: verifies the local environment end to end and
/// reports the first thing that is wrong, in plain language. This is the
/// command a user runs when "it does not work", so every message must be
/// actionable, not a stack trace.
/// </summary>
internal sealed class DoctorCommand : AsyncCommand<EmptySettings>
{
    private readonly ISecretVault _vault;

    public DoctorCommand(ISecretVault vault) => _vault = vault;

    public override async Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        CliOut.Banner();
        AnsiConsole.MarkupLine("[bold]Environment check[/] — the first failing check is usually your problem.\n");

        var problems = 0;

        problems += Check(".NET SDK 9", CheckDotNet);
        problems += Check("Docker daemon", () => LifecycleCommands.RunAndCapture("docker info") is 0
            ? (true, "Docker is running.")
            : (false, "Docker is not running. Start Docker Desktop or `sudo systemctl start docker`."));

        problems += Check("Docker Compose", () =>
        {
            var compose = LifecycleCommands.ResolveComposeCommand();
            return string.IsNullOrEmpty(compose)
                ? (false, "Docker Compose not found. Install the docker-compose-plugin.")
                : (true, $"{compose} available.");
        });

        problems += Check("Repository (ForexTradingBot.sln)", () =>
            LifecycleCommands.FindSolutionRoot() is { } root
                ? (true, root)
                : (false, "Not inside the repository. Clone it first: git clone https://github.com/Opselon/ForexTradingBot.git"));

        problems += Check("Local secret vault", () =>
        {
            if (!_vault.Exists())
            {
                return (true, "No vault yet — optional. Create one with: forexbot secrets set TELEGRAM_BOT_TOKEN");
            }
            var count = _vault.List().Count;
            return (true, $"{_vault.VaultPath} ({count} secret(s))");
        });

        problems += Check("API responding on :8080/healthz", () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                var response = http.GetAsync("http://localhost:8080/healthz").GetAwaiter().GetResult();
                return response.IsSuccessStatusCode
                    ? (true, "API is healthy.")
                    : (false, $"API answered HTTP {(int)response.StatusCode}. Check logs: forexbot logs");
            }
            catch
            {
                return (false, "Nothing is listening on :8080. Start the app: forexbot start");
            }
        });

        AnsiConsole.WriteLine();
        if (problems == 0)
        {
            CliOut.Ok("Everything checks out. The app should be usable.");
            return 0;
        }

        CliOut.Error($"{problems} check(s) failed. Fix the ones above, then re-run: forexbot doctor");
        return 1;
    }

    private static int Check(string name, Func<(bool Ok, string Message)> check)
    {
        try
        {
            var (ok, message) = check();
            AnsiConsole.MarkupLine(ok ? $"[green]✓[/] {Markup.Escape(name)}" : $"[red]✗[/] {Markup.Escape(name)}");
            AnsiConsole.MarkupLine(ok ? $"    [dim]{Markup.Escape(message)}[/]" : $"    [yellow]{Markup.Escape(message)}[/]");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(name)}");
            AnsiConsole.MarkupLine($"    [yellow]Check crashed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static (bool, string) CheckDotNet()
    {
        var version = Environment.Version;
        if (version.Major >= 9)
        {
            return (true, $".NET runtime {version} present.");
        }

        var exit = LifecycleCommands.RunAndCapture("dotnet --list-sdks");
        return exit is 0
            ? (true, "dotnet found on PATH.")
            : (false, ".NET 9 SDK not found. Install it: https://dotnet.microsoft.com/download or run install.sh");
    }
}

/// <summary>
/// Prints the last N log lines from the running container or the local log file.
/// </summary>
internal sealed class LogsCommand : AsyncCommand<LogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[COUNT]")]
        [Description("Number of lines to show")]
        [DefaultValue(80)]
        public int Count { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var root = LifecycleCommands.FindSolutionRoot();
        var compose = LifecycleCommands.ResolveComposeCommand();

        if (!string.IsNullOrEmpty(compose) && root is not null)
        {
            LifecycleCommands.RunAndCapture($"{compose} logs --tail {settings.Count} forex-trading-bot-app", root);
            return 0;
        }

        var logDir = Path.Combine(root ?? Environment.CurrentDirectory, "logs");
        if (Directory.Exists(logDir))
        {
            var latest = new DirectoryInfo(logDir).GetFiles("*.log").MaxBy(f => f.LastWriteTime);
            if (latest is not null)
            {
                var lines = File.ReadLines(latest.FullName).TakeLast(settings.Count);
                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }
                return 0;
            }
        }

        CliOut.Warn("No logs found. Is the app running?");
        return 1;
    }
}
