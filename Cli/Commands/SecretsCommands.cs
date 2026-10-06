using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace ForexTradingBot.Cli.Commands;

internal sealed class SecretsSetCommand : AsyncCommand<SecretsSetCommand.Settings>
{
    private readonly ISecretVault _vault;

    public SecretsSetCommand(ISecretVault vault) => _vault = vault;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<KEY>")]
        [Description("The secret key, e.g. TELEGRAM_BOT_TOKEN")]
        public required string Key { get; init; }

        [CommandArgument(1, "[VALUE]")]
        [Description("The value. Omit to be prompted securely (recommended — keeps it out of shell history)")]
        public string? Value { get; init; }

        [CommandOption("--category")]
        [Description("Category: Telegram, CryptoPay, Database, Redis, Api, Smtp, Other")]
        [DefaultValue(SecretCategory.Other)]
        public SecretCategory Category { get; init; }

        [CommandOption("--description")]
        [Description("Free-text note about what this secret is for")]
        public string? Description { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var value = settings.Value;
        if (string.IsNullOrEmpty(value))
        {
            value = AnsiConsole.Prompt(
                new TextPrompt<string>($"Enter the value for [cyan]{Markup.Escape(settings.Key)}[/]:")
                    .Secret());
        }

        _vault.Set(settings.Key, value, settings.Category, settings.Description);
        CliOut.Ok($"Secret [cyan]{Markup.Escape(settings.Key)}[/] stored in the local vault ({_vault.VaultPath})");
        return Task.FromResult(0);
    }
}

internal sealed class SecretsGetCommand : AsyncCommand<SecretsGetCommand.Settings>
{
    private readonly ISecretVault _vault;

    public SecretsGetCommand(ISecretVault vault) => _vault = vault;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<KEY>")]
        public required string Key { get; init; }

        [CommandOption("--copy")]
        [Description("Copy the value to the clipboard instead of printing it (Windows/macOS)")]
        [DefaultValue(false)]
        public bool Copy { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var value = _vault.Get(settings.Key);
        if (value is null)
        {
            CliOut.Error($"No secret named '{settings.Key}' in the vault.");
            return Task.FromResult(1);
        }

        if (settings.Copy)
        {
            // Clipboard access needs an external helper; fall back to printing.
            CliOut.Warn("--copy is not available on this platform; printing instead.");
        }

        // Print raw so it can be piped. Never marked up.
        Console.WriteLine(value);
        return Task.FromResult(0);
    }
}

internal sealed class SecretsDeleteCommand : AsyncCommand<SecretsDeleteCommand.Settings>
{
    private readonly ISecretVault _vault;

    public SecretsDeleteCommand(ISecretVault vault) => _vault = vault;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<KEY>")]
        public required string Key { get; init; }

        [CommandOption("--yes")]
        [Description("Skip the confirmation prompt")]
        [DefaultValue(false)]
        public bool Yes { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (_vault.Get(settings.Key) is null)
        {
            CliOut.Error($"No secret named '{settings.Key}' in the vault.");
            return Task.FromResult(1);
        }

        if (!settings.Yes && !AnsiConsole.Confirm($"Delete secret [cyan]{Markup.Escape(settings.Key)}[/]? This cannot be undone."))
        {
            CliOut.Info("Aborted.");
            return Task.FromResult(0);
        }

        _vault.Delete(settings.Key);
        CliOut.Ok($"Secret [cyan]{Markup.Escape(settings.Key)}[/] deleted.");
        return Task.FromResult(0);
    }
}

internal sealed class SecretsRotateCommand : AsyncCommand<EmptySettings>
{
    private readonly ISecretVault _vault;

    public SecretsRotateCommand(ISecretVault vault) => _vault = vault;

    public override Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
        if (!_vault.Exists())
        {
            CliOut.Warn("No vault to rotate.");
            return Task.FromResult(0);
        }

        var count = _vault.List().Count;
        AnsiConsole.Status().Start("Re-encrypting vault with a fresh key…", _ => _vault.Rotate());
        CliOut.Ok($"Re-encrypted {count} secret(s) with a new key derived from this machine + user.");
        return Task.FromResult(0);
    }
}
