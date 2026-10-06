using System.Security.Cryptography;
using ForexTradingBot.Cli.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            CliOut.Error("Secret key must not be empty.");
            return 1;
        }

        if (settings.Value is not null && string.IsNullOrWhiteSpace(settings.Value))
        {
            CliOut.Error("Secret value must not be empty.");
            return 1;
        }

        var value = settings.Value;
        if (value is null)
        {
            if (Console.IsInputRedirected)
            {
                CliOut.Error("Interactive secret input is unavailable when stdin is redirected. Pass VALUE explicitly.");
                return 1;
            }

            value = AnsiConsole.Prompt(
                new TextPrompt<string>($"Enter the value for [cyan]{Markup.Escape(settings.Key)}[/]:")
                    .Secret()
                    .Validate(input =>
                        !string.IsNullOrWhiteSpace(input),
                        "Secret value must not be empty."));
        }

        _vault.Set(settings.Key, value, settings.Category, settings.Description);
        CliOut.Ok($"Secret [cyan]{Markup.Escape(settings.Key)}[/] stored in the local vault ({_vault.VaultPath})");
        return 0;
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
        [Description("Copy the value to the clipboard instead of printing it (when a supported clipboard backend is available)")]
        [DefaultValue(false)]
        public bool Copy { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            CliOut.Error("Secret key must not be empty.");
            return 1;
        }

        string? value;
        try
        {
            value = _vault.Get(settings.Key);
        }
        catch (CryptographicException)
        {
            CliOut.Error("The secret vault could not decrypt this value. The local vault key or data may be invalid or changed.");
            return 1;
        }
        catch (FormatException)
        {
            CliOut.Error("The stored secret data is malformed and could not be read.");
            return 1;
        }

        if (value is null)
        {
            CliOut.Error($"No secret named '{settings.Key}' in the vault.");
            return 1;
        }

        if (settings.Copy)
        {
            if (SecretClipboard.TryCopy(value))
            {
                CliOut.Ok("Secret copied to the clipboard. It was not printed.");
                return 0;
            }

            CliOut.Error("Clipboard integration is unavailable on this system. The secret was not printed.");
            return 1;
        }

        // Print raw so it can be piped. Never marked up.
        Console.WriteLine(value);
        return 0;
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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            CliOut.Error("Secret key must not be empty.");
            return 1;
        }

        if (_vault.Get(settings.Key) is null)
        {
            CliOut.Error($"No secret named '{settings.Key}' in the vault.");
            return 1;
        }

        if (!settings.Yes && !AnsiConsole.Confirm($"Delete secret [cyan]{Markup.Escape(settings.Key)}[/]? This cannot be undone."))
        {
            CliOut.Info("Aborted.");
            return 0;
        }

        _vault.Delete(settings.Key);
        CliOut.Ok($"Secret [cyan]{Markup.Escape(settings.Key)}[/] deleted.");
        return 0;
    }
}

internal sealed class SecretsRotateCommand : AsyncCommand<EmptySettings>
{
    private readonly ISecretVault _vault;

    public SecretsRotateCommand(ISecretVault vault) => _vault = vault;

    public override async Task<int> ExecuteAsync(CommandContext context, EmptySettings settings, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (!_vault.Exists())
        {
            CliOut.Warn("No vault to rotate.");
            return 0;
        }

        var count = _vault.List().Count;
        AnsiConsole.Status().Start("Re-encrypting vault with a fresh key…", _ => _vault.Rotate());
        CliOut.Ok($"Re-encrypted {count} secret(s) with a fresh vault salt; the local master key remains unchanged.");
        return 0;
    }
}

internal static class SecretClipboard
{
    public static bool TryCopy(string value)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return RunWithStdin(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command",
                        "$value = [Console]::In.ReadToEnd(); Set-Clipboard -Value $value"],
                    value);
            }

            if (OperatingSystem.IsMacOS())
            {
                return RunWithStdin("pbcopy", [], value);
            }

            if (OperatingSystem.IsLinux())
            {
                if (RunWithStdin("wl-copy", [], value))
                    return true;

                if (RunWithStdin("xclip", ["-selection", "clipboard"], value))
                    return true;

                return RunWithStdin("xsel", ["--clipboard", "--input"], value);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunWithStdin(
        string fileName,
        IReadOnlyList<string> arguments,
        string value)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return false;

        process.StandardInput.Write(value);
        process.StandardInput.Close();

        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        return process.ExitCode == 0;
    }
}
