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

            AnsiConsole.Status().Start("Building and starting the stack (first build takes a few minutes)…", _ =>
                LifecycleCommands.RunAndCapture($"{compose} up -d --build", root));

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
        var root = LifecycleCommands.FindSolutionRoot();
        if (root is null)
        {
            CliOut.Error("Not inside the repository.");
            return 1;
        }

        CliOut.Info("Checking the EF tool…");
        LifecycleCommands.RunAndCapture("dotnet tool restore", root);

        var args = string.IsNullOrEmpty(settings.Connection)
            ? "database update"
            : $"database update --connection \"{settings.Connection}\"";

        AnsiConsole.Status().Start("Applying migrations…", _ =>
            LifecycleCommands.RunAndCapture($"dotnet ef {args} --project Infrastructure --startup-project WebAPI", root));

        CliOut.Ok("Migrations applied.");
        return 0;
    }
}

/// <summary>Lists applied migrations so a user can see the schema state.</summary>
internal sealed class MigrationsListCommand : AsyncCommand<EmptySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
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
internal sealed class ConfigShowCommand : AsyncCommand<EmptySettings>
{
    private readonly ISecretVault _vault;

    public ConfigShowCommand(ISecretVault vault) => _vault = vault;

    public override async Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
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
