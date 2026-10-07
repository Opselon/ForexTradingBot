using ForexTradingBot.Cli.Secrets;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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
        await Task.CompletedTask;
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

        try
        {
            _vault.BackupTo(targetFullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            CliOut.Error($"Backup failed: {ex.Message}");
            return 1;
        }

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
        await Task.CompletedTask;
        if (!File.Exists(settings.Path))
        {
            CliOut.Error($"Backup file not found: {settings.Path}");
            return 1;
        }

        var backupPath = Path.GetFullPath(settings.Path);
        var legacySaltPath = Path.ChangeExtension(backupPath, ".salt");

        // AnsiConsole.Confirm throws when stdin is redirected (headless/CI/piped),
        // so fall back to reading a plain line from stdin. Only refuse when there is
        // genuinely no answer to read.
        if (!CliConfirm.TryAsk("Restoring [red]overwrites[/] the current vault. Continue?", out var confirmed, defaultValue: false))
        {
            CliOut.Error("Cannot ask for confirmation: no answer on stdin. Run 'forexbot restore <PATH>' from an interactive terminal, or pipe an answer (e.g. 'y').");
            return 1;
        }

        if (!confirmed)
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

            // Fail closed on a truncated or tampered file before touching the live
            // vault: a corrupted SQLite image can otherwise open "successfully" and
            // silently replace good data with an empty or partial table.
            if (!BackupIntegrity.IsReadableSecretsDatabase(stagedDb))
            {
                CliOut.Error($"The backup at {backupPath} is not a valid secrets vault (integrity check failed). Nothing was changed.");
                return 1;
            }

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
