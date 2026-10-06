using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace ForexTradingBot.Cli.Commands;

/// <summary>
/// Lifecycle commands for the running app. They operate on the local machine
/// only: Docker Compose when a compose file is present, otherwise a plain
/// <c>dotnet run</c>. Nothing here touches the network beyond the app itself.
/// </summary>
internal static class LifecycleCommands
{
    internal static string? FindSolutionRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (dir.GetFiles("ForexTradingBot.sln").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    internal static string ResolveComposeCommand()
    {
        if (RunAndCapture("docker compose version") is 0)
        {
            return "docker compose";
        }
        if (File.Exists("/usr/bin/docker-compose") || File.Exists("/usr/local/bin/docker-compose"))
        {
            return "docker-compose";
        }
        return string.Empty;
    }

    internal static int RunExecutableAndCapture(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var proc = Process.Start(psi);
            if (proc is null)
                return -1;

            if (!proc.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                return -1;
            }
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    internal static int RunAndCapture(string command, string? workingDirectory = null)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var escaped = command.Replace("\"", "\\\"");
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                Arguments = isWindows ? $"/c \"{escaped}\"" : $"-c \"{escaped}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return -1;
            }
            // External diagnostics (docker info, compose ps) can block indefinitely on
            // CI runners where the daemon is unreachable; a hung probe must not hang the CLI.
            if (!proc.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                return -1;
            }
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}

internal sealed class StartCommand : AsyncCommand<StartCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--build")]
        [Description("Rebuild the Docker image before starting")]
        [DefaultValue(false)]
        public bool Build { get; init; }

        [CommandOption("--native")]
        [Description("Run with dotnet run instead of Docker")]
        [DefaultValue(false)]
        public bool Native { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // E2E guard: the harness sets FOREXBOT_VAULT_DIRECTORY to a scratch path. Never
        // run docker compose / dotnet in that mode; fail closed and stay fast.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY")))
        {
            CliOut.Error("Refusing to run this command: a temporary vault directory is set (E2E mode).");
            return 1;
        }

        await Task.CompletedTask;
        var root = LifecycleCommands.FindSolutionRoot()
            ?? throw new InvalidOperationException("Could not find ForexTradingBot.sln. Run this from the repository directory.");
        var compose = LifecycleCommands.ResolveComposeCommand();

        if (settings.Native || string.IsNullOrEmpty(compose))
        {
            CliOut.Info("Starting with 'dotnet run' (native mode)…");
            return LifecycleCommands.RunAndCapture("dotnet run --project WebAPI -c Release", root);
        }

        int startExit = 0;
        AnsiConsole.Status().Start("Starting the stack (Postgres, Redis, API)…", _ =>
        {
            var args = settings.Build ? "up -d --build" : "up -d";
            startExit = LifecycleCommands.RunAndCapture($"{compose} {args}", root);
        });

        if (startExit != 0)
        {
            CliOut.Error($"Could not start the stack (exit code {startExit}).");
            return startExit;
        }

        CliOut.Ok("Stack started. Check status with: forexbot status");
        return 0;
    }
}

internal sealed class StopCommand : AsyncCommand<StopCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--volumes")]
        [Description("Also remove Docker volumes (DELETES database data)")]
        [DefaultValue(false)]
        public bool Volumes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // E2E guard: the harness sets FOREXBOT_VAULT_DIRECTORY to a scratch path. Never
        // run docker compose / dotnet in that mode; fail closed and stay fast.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY")))
        {
            CliOut.Error("Refusing to run this command: a temporary vault directory is set (E2E mode).");
            return 1;
        }

        await Task.CompletedTask;
        var root = LifecycleCommands.FindSolutionRoot();
        if (root is null)
        {
            CliOut.Error("Could not find ForexTradingBot.sln.");
            return 1;
        }

        var compose = LifecycleCommands.ResolveComposeCommand();
        if (string.IsNullOrEmpty(compose))
        {
            CliOut.Warn("Docker Compose not found; stopping the local WebAPI process instead.");
            return OperatingSystem.IsWindows()
                ? LifecycleCommands.RunAndCapture("taskkill /F /IM dotnet.exe /T", root)
                : LifecycleCommands.RunAndCapture("pkill -f 'dotnet.*WebAPI'", root);
        }

        if (settings.Volumes)
        {
            // AnsiConsole.Confirm throws when stdin is redirected (headless/CI/piped).
            if (!CliConfirm.TryAsk("Remove volumes too? This [red]deletes the database data[/].", out var confirmedVolumes, defaultValue: false))
            {
                CliOut.Error("Cannot ask for confirmation: no answer on stdin. Run 'forexbot stop --volumes' from an interactive terminal to approve the data wipe.");
                return 1;
            }

            if (!confirmedVolumes)
            {
                return 0;
            }
        }

        var args = settings.Volumes ? "down -v" : "down";
        int exit = 0;
        AnsiConsole.Status().Start("Stopping the stack…", _ => exit = LifecycleCommands.RunAndCapture($"{compose} {args}", root));
        if (exit != 0)
        {
            CliOut.Error($"Could not stop the stack (exit code {exit}).");
            return exit;
        }

        CliOut.Ok("Stack stopped.");
        return 0;
    }
}

internal sealed class StatusCommand : AsyncCommand<EmptySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var root = LifecycleCommands.FindSolutionRoot();
        if (root is null)
        {
            CliOut.Error("Could not find ForexTradingBot.sln.");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Check");
        table.AddColumn("Result");

        // Docker
        var dockerOk = LifecycleCommands.RunAndCapture("docker info") is 0;
        table.AddRow("Docker daemon", dockerOk ? "[green]running[/]" : "[red]not running[/]");

        // API health
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var response = await http.GetAsync("http://localhost:8080/healthz");
            table.AddRow("API /healthz", response.IsSuccessStatusCode ? "[green]healthy[/]" : $"[yellow]{(int)response.StatusCode}[/]");
        }
        catch
        {
            table.AddRow("API /healthz", "[red]not reachable[/]");
        }

        // Vault
        var compose = LifecycleCommands.ResolveComposeCommand();
        if (!string.IsNullOrEmpty(compose))
        {
            var ps = LifecycleCommands.RunAndCapture($"{compose} ps", root);
            table.AddRow("Compose services", ps is 0 ? "[green]ok[/]" : "[red]error — see above[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
