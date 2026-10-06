using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace ForexTradingBot.Cli.Commands;

internal sealed class SecretsListCommand : AsyncCommand<SecretsListCommand.Settings>
{
    private readonly ISecretVault _vault;

    public SecretsListCommand(ISecretVault vault) => _vault = vault;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--show-values")]
        [Description("Decrypt and print the actual values (dangerous — visible in your terminal history)")]
        [DefaultValue(false)]
        public bool ShowValues { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!_vault.Exists())
        {
            CliOut.Warn($"No vault yet at {_vault.VaultPath}");
            CliOut.Info("Add a secret with: forexbot secrets set <key>");
            return 0;
        }

        var secrets = _vault.List();
        if (secrets.Count == 0)
        {
            CliOut.Info("Vault is empty.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Key");
        table.AddColumn("Category");
        table.AddColumn("Value");
        table.AddColumn("Updated (UTC)");

        foreach (var secret in secrets)
        {
            var value = settings.ShowValues
                ? Markup.Escape(_vault.Get(secret.Key) ?? string.Empty)
                : "[dim]••••••••[/]";
            table.AddRow(
                Markup.Escape(secret.Key),
                secret.Category.ToString(),
                value,
                secret.UpdatedUtc.ToString("yyyy-MM-dd HH:mm"));
        }

        AnsiConsole.Write(table);

        if (!settings.ShowValues)
        {
            AnsiConsole.WriteLine();
            CliOut.Info("Values are hidden. Re-run with --show-values to reveal them.");
        }
        return 0;
    }
}
