using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace ForexTradingBot.Cli.Commands;

/// <summary>
/// Backs the vault up to a single encrypted file the user controls, and
/// restores from one. The backup is a byte-for-byte copy of the vault plus
/// its salt, so it stays encrypted with the machine+user key of whoever made
/// it — restoring on a different machine requires re-entering the secrets.
/// </summary>
internal sealed class BackupCommand : AsyncCommand<BackupCommand.Settings>
{
    private readonly ISecretVault _vault;

    public BackupCommand(ISecretVault vault) => _vault = vault;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PATH]")]
        [Description("Where to write the backup (default: ./secrets-backup-<date>.db")]
        public string? Path { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!_vault.Exists())
        {
            CliOut.Warn("The vault is empty — nothing to back up.");
            return Task.FromResult(0);
        }

        var target = settings.Path ?? $"secrets-backup-{DateTime.UtcNow:yyyyMMdd}.db";
        var saltPath = Path.Combine(Path.GetDirectoryName(_vault.VaultPath)!, "secrets.salt");
        var saltTarget = Path.ChangeExtension(target, ".salt");

        File.Copy(_vault.VaultPath, target, overwrite: true);
        if (File.Exists(saltPath))
        {
            File.Copy(saltPath, saltTarget, overwrite: true);
        }

        CliOut.Ok($"Vault backed up to {target}");
        CliOut.Info($"Salt copied to {saltTarget} — keep both files together.");
        CliOut.Warn("The backup stays encrypted with this machine's key; it cannot be read elsewhere.");
        return Task.FromResult(0);
    }
}

internal sealed class RestoreCommand : AsyncCommand<RestoreCommand.Settings>
{
    private readonly ISecretVault _vault;

    public RestoreCommand(ISecretVault vault) => _vault = vault;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        [Description("The backup file created by 'forexbot backup'")]
        public required string Path { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Path))
        {
            CliOut.Error($"Backup file not found: {settings.Path}");
            return Task.FromResult(1);
        }

        var saltTarget = Path.ChangeExtension(settings.Path, ".salt");
        if (!File.Exists(saltTarget))
        {
            CliOut.Error($"Missing salt file: {saltTarget}. Both files are required to restore.");
            return Task.FromResult(1);
        }

        if (!AnsiConsole.Confirm("Restoring [red]overwrites[/] the current vault. Continue?"))
        {
            return Task.FromResult(0);
        }

        File.Copy(settings.Path, _vault.VaultPath, overwrite: true);
        var saltDest = Path.Combine(Path.GetDirectoryName(_vault.VaultPath)!, "secrets.salt");
        File.Copy(saltTarget, saltDest, overwrite: true);

        CliOut.Ok($"Vault restored from {settings.Path}");
        return Task.FromResult(0);
    }
}
