using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using ForexTradingBot.Cli.Commands;
using ForexTradingBot.Cli.Secrets;

namespace ForexTradingBot.Cli;

/// <summary>
/// Wires the CLI together. The vault and cipher are singletons for the whole
/// process lifetime (the SQLite connection is not thread-safe to reopen per
/// command), and every command that needs the vault takes it via DI.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretCipher>(_ => new SecretCipher(SecretKeyStore.LoadOrCreateKey(SqliteSecretVault.DefaultVaultDirectory())));
        services.AddSingleton<ISecretVault, SqliteSecretVault>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.SetApplicationName("forexbot");
            config.SetApplicationVersion(typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0");

            config.AddCommand<InstallCommand>("install")
                .WithDescription("One-command install: Docker stack (recommended) or native .NET, with health gating.");

            config.AddCommand<StartCommand>("start")
                .WithDescription("Start the app (Docker or native).");

            config.AddCommand<StopCommand>("stop")
                .WithDescription("Stop the running app. Use --volumes to also delete data (dangerous).");

            config.AddCommand<StatusCommand>("status")
                .WithDescription("Show Docker, API health, and vault status at a glance.");

            config.AddCommand<DoctorCommand>("doctor")
                .WithDescription("Diagnose the local environment; the first failing check is usually the problem.");

            config.AddCommand<LogsCommand>("logs")
                .WithDescription("Tail the app logs (Docker or local log file).");

            config.AddCommand<MigrateCommand>("migrate")
                .WithDescription("Apply EF Core database migrations to the configured database.");

            config.AddCommand<MigrationsListCommand>("migrations")
                .WithDescription("List applied and pending database migrations.");

            config.AddBranch("secrets", secrets =>
            {
                secrets.SetDescription("Encrypted-at-rest secret vault stored on THIS machine (never synced).");

                secrets.AddCommand<SecretsListCommand>("list")
                    .WithDescription("List stored secrets (values hidden unless --show-values).")
                    .WithExample("secrets", "list", "--show-values");

                secrets.AddCommand<SecretsGetCommand>("get")
                    .WithDescription("Print a single secret's value (can be piped).")
                    .WithExample("secrets", "get", "TELEGRAM_BOT_TOKEN");

                secrets.AddCommand<SecretsSetCommand>("set")
                    .WithDescription("Store a secret. Omit the value to be prompted securely.")
                    .WithExample("secrets", "set", "TELEGRAM_BOT_TOKEN", "--category", "Telegram");

                secrets.AddCommand<SecretsDeleteCommand>("delete")
                    .WithDescription("Delete a secret (asks first).");

                secrets.AddCommand<SecretsRotateCommand>("rotate")
                    .WithDescription("Re-encrypt every secret with a brand-new machine+user key.");
            });

            config.AddCommand<BackupCommand>("backup")
                .WithDescription("Copy the encrypted vault to a file you control.");

            config.AddCommand<RestoreCommand>("restore")
                .WithDescription("Overwrite the vault from a backup file.");

            config.AddCommand<ConfigShowCommand>("config")
                .WithDescription("Show the public configuration (secrets and connection strings never printed).");
        });

        return app.Run(args);
    }
}
