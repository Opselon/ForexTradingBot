using ForexTradingBot.Cli.Secrets;
using System.Security.Cryptography;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace ForexTradingBot.Cli.Commands;

/// <summary>
/// Backs up the encrypted vault database. The active vault salt is embedded
/// in VaultMetadata, so new backups are self-contained. Legacy sidecar salts
/// are accepted during restore for backward compatibility.
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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!_vault.Exists())
        {
            CliOut.Warn("The vault is empty — nothing to back up.");
            return 0;
        }

        var target = settings.Path ?? $"secrets-backup-{DateTime.UtcNow:yyyyMMdd}.db";
        var targetFullPath = Path.GetFullPath(target);
        var vaultFullPath = Path.GetFullPath(_vault.VaultPath);

        if (string.Equals(targetFullPath, vaultFullPath, StringComparison.OrdinalIgnoreCase))
        {
            CliOut.Error("Backup destination must be different from the live vault.");
            return 1;
        }

        _vault.BackupTo(targetFullPath);

        CliOut.Ok($"Vault backed up to {targetFullPath}");
        CliOut.Info("The backup is self-contained and encrypted at rest. Keep it as carefully as the live vault.");
        return 0;
    }
}

internal sealed class RestoreCommand : AsyncCommand<RestoreCommand.Settings>
{
    private readonly ISecretVault _vault;
    private readonly ISecretCipher _cipher;

    public RestoreCommand(ISecretVault vault, ISecretCipher cipher)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        [Description("The backup file created by 'forexbot backup'")]
        public required string Path { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Path))
        {
            CliOut.Error($"Backup file not found: {settings.Path}");
            return 1;
        }

        var backupPath = Path.GetFullPath(settings.Path);
        var legacySaltPath = Path.ChangeExtension(backupPath, ".salt");

        if (!AnsiConsole.Confirm("Restoring [red]overwrites[/] the current vault. Continue?"))
        {
            CliOut.Info("Aborted.");
            return 0;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "forexbot-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var stagedDb = Path.Combine(tempDirectory, "secrets.db");
            File.Copy(backupPath, stagedDb, overwrite: false);

            if (File.Exists(legacySaltPath))
                File.Copy(legacySaltPath, Path.Combine(tempDirectory, "secrets.salt"), overwrite: false);

            using var backupVault = new SqliteSecretVault(_cipher, tempDirectory);

            // SqliteSecretVault validates and decrypts the full source before
            // committing the replacement, so a corrupt backup cannot leave
            // the live vault half-restored.
            _vault.RestoreFrom(backupVault);

            CliOut.Ok($"Vault restored from {backupPath}");
            return 0;
        }
        catch (CryptographicException)
        {
            CliOut.Error("The backup could not be decrypted with this user's local vault key. Nothing was changed.");
            return 1;
        }
        catch (Exception ex)
        {
            CliOut.Error($"Vault restore failed: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }
}
