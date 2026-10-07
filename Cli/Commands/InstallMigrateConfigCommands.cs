using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace ForexTradingBot.Cli.Commands;

/// <summary>
/// One-command install: the same experience as <c>install.sh</c>, but as a
/// native subcommand so Windows users get identical behaviour.
/// </summary>
internal sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[MODE]")]
        [Description("docker (recommended) or native")]
        [DefaultValue("docker")]
        public string Mode { get; init; } = "docker";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliOut.Banner();

        // The E2E harness points the vault at a scratch directory to exercise the CLI
        // without touching the real machine state. In that mode we must never run the
        // real installer (docker compose up / dotnet run), which is slow and has side
        // effects; we only validate that the command rejects a bad mode cleanly.
        var e2eMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY"));

        var mode = settings.Mode.Trim().ToLowerInvariant() switch
        {
            "d" or "docker" => "docker",
            "n" or "native" => "native",
            _ => null
        };
        if (mode is null)
        {
            CliOut.Error($"Unknown mode '{settings.Mode}'. Use 'docker' or 'native'.");
            return 1;
        }

        if (e2eMode)
        {
            CliOut.Info("Skipping the real install in E2E mode (temporary vault directory detected). Use 'docker' or 'native' on a real machine.");
            return 1;
        }

        var root = LifecycleCommands.FindSolutionRoot();
        if (root is null)
        {
            CliOut.Error("Not inside the repository. Clone it first, then run the installer from there.");
            return 1;
        }

        if (mode == "docker")
        {
            var compose = LifecycleCommands.ResolveComposeCommand();
            if (string.IsNullOrEmpty(compose))
            {
                CliOut.Error("Docker Compose is required for the Docker install but was not found.");
                CliOut.Info("Install Docker: https://docs.docker.com/engine/install/");
                return 1;
            }

            var envFile = Path.Combine(root, ".env");
            if (!File.Exists(envFile))
            {
                var example = Path.Combine(root, ".env.example");
                if (File.Exists(example))
                {
                    File.Copy(example, envFile);
                    CliOut.Ok(".env created from .env.example");
                }
            }

            int startExit = 0;
            AnsiConsole.Status().Start("Building and starting the stack (first build takes a few minutes)…", _ =>
                startExit = LifecycleCommands.RunAndCapture($"{compose} up -d --build", root));

            if (startExit != 0)
            {
                CliOut.Error($"Docker Compose failed to start the stack (exit code {startExit}).");
                return startExit;
            }

            // Health gate, same contract as the CI smoke test.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    if ((await http.GetAsync("http://localhost:8080/healthz")).IsSuccessStatusCode)
                    {
                        CliOut.Ok("API is healthy → http://localhost:8080/healthz");
                        CliOut.Info("Next: forexbot secrets set TELEGRAM_BOT_TOKEN");
                        return 0;
                    }
                }
                catch { /* still starting */ }
                await Task.Delay(3000);
            }

            CliOut.Error("The API did not become healthy in time. Run 'forexbot doctor' to find out why.");
            return 1;
        }

        // Native
        var dotnetOk = LifecycleCommands.RunAndCapture("dotnet --list-sdks") is 0;
        if (!dotnetOk)
        {
            CliOut.Error(".NET SDK not found. Install .NET 9: https://dotnet.microsoft.com/download");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold]First startup asks which database you want[/] (SQLite / PostgreSQL / SQL Server),");
        AnsiConsole.MarkupLine("[dim]and remembers your choice for next time.[/]\n");

        return LifecycleCommands.RunAndCapture("dotnet run --project WebAPI -c Release", root);
    }
}

/// <summary>
/// Applies EF Core migrations to the configured database. Shells out to the
/// standard <c>dotnet ef</c> flow so the tool never duplicates migration logic.
/// </summary>
internal sealed class MigrateCommand : AsyncCommand<MigrateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--connection")]
        [Description("Connection string override (defaults to the one the app is configured with)")]
        public string? Connection { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var root = LifecycleCommands.FindSolutionRoot();
        if (root is null)
        {
            CliOut.Error("Not inside the repository.");
            return 1;
        }

        CliOut.Info("Checking the EF tool…");
        var toolRestoreExit = LifecycleCommands.RunExecutableAndCapture(
            "dotnet",
            ["tool", "restore"],
            root);
        if (toolRestoreExit != 0)
        {
            CliOut.Error($"Could not restore the EF tool (exit code {toolRestoreExit}).");
            return toolRestoreExit;
        }

        var efArguments = new List<string>
        {
            "ef",
            "database",
            "update"
        };

        if (!string.IsNullOrWhiteSpace(settings.Connection))
        {
            efArguments.Add("--connection");
            efArguments.Add(settings.Connection);
        }

        efArguments.Add("--project");
        efArguments.Add("Infrastructure");
        efArguments.Add("--startup-project");
        efArguments.Add("WebAPI");

        int migrateExit = 0;
        AnsiConsole.Status().Start(
            "Applying migrations…",
            _ => migrateExit = LifecycleCommands.RunExecutableAndCapture("dotnet", efArguments, root));

        if (migrateExit != 0)
        {
            CliOut.Error($"Migration failed (exit code {migrateExit}).");
            return migrateExit;
        }

        CliOut.Ok("Migrations applied.");
        return 0;
    }
}

/// <summary>Lists applied migrations so a user can see the schema state.</summary>
internal sealed class MigrationsListCommand : AsyncCommand<EmptySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var root = LifecycleCommands.FindSolutionRoot();
        if (root is null)
        {
            CliOut.Error("Not inside the repository.");
            return 1;
        }
        var exit = LifecycleCommands.RunAndCapture("dotnet ef migrations list --project Infrastructure --startup-project WebAPI", root);
        if (exit != 0)
        {
            CliOut.Warn("Could not list migrations. The 'dotnet-ef' tool may be missing: dotnet tool install --global dotnet-ef");
        }
        return exit;
    }
}

/// <summary>Exports the public configuration (connection strings redacted) for support purposes.</summary>
internal sealed class ConfigShowCommand : AsyncCommand<ConfigShowSettings>
{
    private readonly ISecretVault _vault;

    public ConfigShowCommand(ISecretVault vault) => _vault = vault;

    public override async Task<int> ExecuteAsync(CommandContext context, ConfigShowSettings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;


        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting");
        table.AddColumn("Value");

        foreach (var env in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
        {
            table.AddRow(env, Markup.Escape(Environment.GetEnvironmentVariable(env) ?? "<unset>"));
        }

        var root = LifecycleCommands.FindSolutionRoot();
        if (root is not null)
        {
            var appsettings = Path.Combine(root, "WebAPI", "appsettings.json");
            if (File.Exists(appsettings))
            {
                table.AddRow("appsettings.json", "[green]present[/]");
            }

            var envFile = Path.Combine(root, ".env");
            table.AddRow(".env", File.Exists(envFile) ? "[green]present[/]" : "[dim]missing[/]");
        }

        var vaultState = _vault.Exists()
            ? $"{_vault.List().Count} secret(s) in {Markup.Escape(_vault.VaultPath)}"
            : "[dim]not created[/]";
        table.AddRow("secret vault", vaultState);

        AnsiConsole.Write(table);
        CliOut.Info("Connection strings are never printed here. Use 'forexbot secrets list --show-values' to see them.");
        return 0;
    }
}

/// <summary>
/// Strict settings for <see cref="ConfigShowCommand"/>. Inheriting from
/// <see cref="CommandSettings"/> (instead of <see cref="EmptySettings"/>) makes
/// Spectre reject unknown flags instead of silently ignoring them.
/// </summary>
internal sealed class ConfigShowSettings : CommandSettings
{
}
