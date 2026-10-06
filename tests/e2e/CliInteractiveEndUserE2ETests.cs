using System.Diagnostics;
using System.Text;
using Xunit;

namespace ForexTradingBot.EndToEnd;

public sealed class CliInteractiveEndUserE2ETests
{
    private readonly string _root;

    public CliInteractiveEndUserE2ETests()
    {
        _root = FindRepositoryRoot();
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "CLI")]
    public async Task Cli_headless_secret_flow_handles_realistic_user_answers()
    {
        var vault = CreateTempDirectory();
        const string key = "CLI_INTERACTIVE_SECRET";
        var secret = "interactive-secret-" + Guid.NewGuid().ToString("N");

        try
        {
            // CI/headless execution redirects stdin, so the secure interactive
            // prompt must not pretend it has a TTY. It should fail closed rather
            // than accidentally echoing or accepting an unsafe input mode.
            var missingValue = await RunCliAsync(
                vault,
                ["secrets", "set", key, "--category", "Api"],
                standardInput: Environment.NewLine + secret + Environment.NewLine);

            Assert.Equal(1, missingValue.ExitCode);
            Assert.Contains("stdin is redirected", missingValue.StdOut + missingValue.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, missingValue.StdOut + missingValue.StdErr, StringComparison.Ordinal);

            // Supplying the value explicitly is the supported non-interactive path.
            var set = await RunCliAsync(
                vault,
                ["secrets", "set", key, secret, "--category", "Api"]);

            Assert.Equal(0, set.ExitCode);
            Assert.DoesNotContain(secret, set.StdOut + set.StdErr, StringComparison.Ordinal);

            // Normal list must redact the secret.
            var list = await RunCliAsync(vault, ["secrets", "list"]);
            Assert.Equal(0, list.ExitCode);
            Assert.Contains(key, list.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, list.StdOut + list.StdErr, StringComparison.Ordinal);

            // Explicit reveal is the only normal CLI flow that may print plaintext.
            var get = await RunCliAsync(vault, ["secrets", "get", key]);
            Assert.Equal(0, get.ExitCode);
            Assert.Contains(secret, get.StdOut, StringComparison.Ordinal);

            // --copy must never fall back to printing a secret.
            var copy = await RunCliAsync(vault, ["secrets", "get", key, "--copy"]);
            Assert.DoesNotContain(secret, copy.StdOut + copy.StdErr, StringComparison.Ordinal);

            // On runners with a supported clipboard backend, the command succeeds.
            // On headless Linux without one, it must fail closed without leaking plaintext.
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(0, copy.ExitCode);
                var clipboard = await ReadClipboardAsync();
                Assert.Equal(secret, clipboard);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Assert.Equal(0, copy.ExitCode);
                var clipboard = await ReadClipboardAsync();
                Assert.Equal(secret, clipboard);
            }
            else if (OperatingSystem.IsLinux())
            {
                var clipboardReader = await TryReadClipboardAsync();
                if (clipboardReader is not null)
                {
                    Assert.Equal(0, copy.ExitCode);
                    Assert.Equal(secret, clipboardReader);
                }
                else
                {
                    Assert.Equal(1, copy.ExitCode);
                }
            }

            // Backup/restore must operate on the new self-contained vault format.
            var backupPath = Path.Combine(vault, "user-backup.db");
            var extraKey = "BACKUP_EXTRA_SECRET";
            const string extraValue = "backup-extra-value";

            var setExtra = await RunCliAsync(
                vault,
                ["secrets", "set", extraKey, extraValue, "--category", "Api"]);
            Assert.Equal(0, setExtra.ExitCode);

            var backup = await RunCliAsync(vault, ["backup", backupPath]);
            Assert.Equal(0, backup.ExitCode);
            Assert.True(File.Exists(backupPath));
            var backupBytes = await File.ReadAllBytesAsync(backupPath);

            var selfBackup = await RunCliAsync(
                vault,
                ["backup", Path.Combine(vault, "secrets.db")]);
            Assert.Equal(1, selfBackup.ExitCode);

            var missingRestore = await RunCliAsync(
                vault,
                ["restore", Path.Combine(vault, "does-not-exist.db")]);
            Assert.Equal(1, missingRestore.ExitCode);
            Assert.DoesNotContain(
                extraValue,
                Encoding.UTF8.GetString(backupBytes),
                StringComparison.Ordinal);

            // A corrupted backup must fail closed and preserve the live vault.
            var corruptBackupPath = Path.Combine(vault, "corrupt-backup.db");
            await File.WriteAllBytesAsync(corruptBackupPath, backupBytes);
            var corruptBytes = await File.ReadAllBytesAsync(corruptBackupPath);
            corruptBytes[Math.Min(64, corruptBytes.Length - 1)] ^= 0x55;
            await File.WriteAllBytesAsync(corruptBackupPath, corruptBytes);

            var corruptRestore = await RunCliAsync(
                vault,
                ["restore", corruptBackupPath],
                standardInput: "y" + Environment.NewLine);
            Assert.Equal(1, corruptRestore.ExitCode);
            Assert.DoesNotContain(secret, corruptRestore.StdOut + corruptRestore.StdErr, StringComparison.Ordinal);

            var mutate = await RunCliAsync(
                vault,
                ["secrets", "set", key, "mutated-value-after-backup", "--category", "Api"]);
            Assert.Equal(0, mutate.ExitCode);

            var addUnbacked = await RunCliAsync(
                vault,
                ["secrets", "set", "UNBACKED_SECRET", "must-disappear", "--category", "Api"]);
            Assert.Equal(0, addUnbacked.ExitCode);

            var declinedRestore = await RunCliAsync(
                vault,
                ["restore", backupPath],
                standardInput: "n" + Environment.NewLine);
            Assert.Equal(0, declinedRestore.ExitCode);

            var stillMutated = await RunCliAsync(vault, ["secrets", "get", key]);
            Assert.Equal(0, stillMutated.ExitCode);
            Assert.Contains("mutated-value-after-backup", stillMutated.StdOut, StringComparison.Ordinal);

            var restore = await RunCliAsync(
                vault,
                ["restore", backupPath],
                standardInput: "y" + Environment.NewLine);
            Assert.Equal(0, restore.ExitCode);

            var restored = await RunCliAsync(vault, ["secrets", "get", key]);
            Assert.Equal(0, restored.ExitCode);
            Assert.Contains(secret, restored.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("mutated-value-after-backup", restored.StdOut, StringComparison.Ordinal);

            var restoredExtra = await RunCliAsync(vault, ["secrets", "get", extraKey]);
            Assert.Equal(0, restoredExtra.ExitCode);
            Assert.Contains(extraValue, restoredExtra.StdOut, StringComparison.Ordinal);

            var unbacked = await RunCliAsync(vault, ["secrets", "get", "UNBACKED_SECRET"]);
            Assert.Equal(1, unbacked.ExitCode);

            var rotateAfterRestore = await RunCliAsync(vault, ["secrets", "rotate"]);
            Assert.Equal(0, rotateAfterRestore.ExitCode);

            var cleanup = await RunCliAsync(
                vault,
                ["secrets", "set", "CLEANUP_SECRET", "cleanup-value", "--category", "Other"]);
            Assert.Equal(0, cleanup.ExitCode);

            var cleanupDelete = await RunCliAsync(
                vault,
                ["secrets", "delete", "CLEANUP_SECRET", "--yes"]);
            Assert.Equal(0, cleanupDelete.ExitCode);

            var cleanupMissing = await RunCliAsync(
                vault,
                ["secrets", "get", "CLEANUP_SECRET"]);
            Assert.Equal(1, cleanupMissing.ExitCode);

            // User says "no": the destructive operation must be cancelled and the value preserved.
            var decline = await RunCliAsync(
                vault,
                ["secrets", "delete", key],
                standardInput: "n" + Environment.NewLine);

            Assert.Equal(0, decline.ExitCode);

            var stillPresent = await RunCliAsync(vault, ["secrets", "get", key]);
            Assert.Equal(0, stillPresent.ExitCode);
            Assert.Contains(secret, stillPresent.StdOut, StringComparison.Ordinal);

            // User gives an invalid confirmation first, then explicitly says "yes".
            var confirm = await RunCliAsync(
                vault,
                ["secrets", "delete", key],
                standardInput: "maybe" + Environment.NewLine + "y" + Environment.NewLine);

            Assert.Equal(0, confirm.ExitCode);

            var missing = await RunCliAsync(vault, ["secrets", "get", key]);
            Assert.Equal(1, missing.ExitCode);
            Assert.DoesNotContain(secret, missing.StdOut + missing.StdErr, StringComparison.Ordinal);

            // Invalid enum input must fail before a secret is written.
            var badCategory = await RunCliAsync(
                vault,
                ["secrets", "set", "SHOULD_NOT_EXIST", "value", "--category", "NotARealCategory"]);

            Assert.NotEqual(0, badCategory.ExitCode);

            var afterBadCategory = await RunCliAsync(vault, ["secrets", "get", "SHOULD_NOT_EXIST"]);
            Assert.Equal(1, afterBadCategory.ExitCode);
        }
        finally
        {
            TryDelete(vault);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "CLI")]
    public async Task Cli_secret_visibility_and_wrong_key_fail_closed()
    {
        var vault = CreateTempDirectory();
        const string key = "CLI_VISIBILITY_SECRET";
        var secret = "visibility-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var set = await RunCliAsync(vault, ["secrets", "set", key, secret, "--category", "Api"]);
            Assert.Equal(0, set.ExitCode);

            var hidden = await RunCliAsync(vault, ["secrets", "list"]);
            Assert.Equal(0, hidden.ExitCode);
            Assert.Contains(key, hidden.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, hidden.StdOut + hidden.StdErr, StringComparison.Ordinal);

            var explicitReveal = await RunCliAsync(vault, ["secrets", "list", "--show-values"]);
            Assert.Equal(0, explicitReveal.ExitCode);
            Assert.Contains(secret, explicitReveal.StdOut, StringComparison.Ordinal);

            var keyPath = Path.Combine(vault, "secrets.key");
            var originalKey = await File.ReadAllBytesAsync(keyPath);
            try
            {
                await File.WriteAllBytesAsync(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

                var wrongKey = await RunCliAsync(vault, ["secrets", "get", key]);
                Assert.Equal(1, wrongKey.ExitCode);
                Assert.Contains("could not decrypt", wrongKey.StdOut + wrongKey.StdErr, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, wrongKey.StdOut + wrongKey.StdErr, StringComparison.Ordinal);
            }
            finally
            {
                await File.WriteAllBytesAsync(keyPath, originalKey);
            }

            var recovered = await RunCliAsync(vault, ["secrets", "get", key]);
            Assert.Equal(0, recovered.ExitCode);
            Assert.Contains(secret, recovered.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(vault);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "CLI")]
    public async Task Cli_invalid_install_mode_and_missing_context_fail_cleanly()
    {
        var vault = CreateTempDirectory();

        try
        {
            var invalidMode = await RunCliAsync(vault, ["install", "definitely-not-a-mode"]);
            Assert.Equal(1, invalidMode.ExitCode);
            Assert.Contains("docker", invalidMode.StdOut + invalidMode.StdErr, StringComparison.OrdinalIgnoreCase);

            var missingKey = await RunCliAsync(vault, ["secrets", "get", "   "]);
            Assert.Equal(1, missingKey.ExitCode);

            var missingDelete = await RunCliAsync(vault, ["secrets", "delete", "DOES_NOT_EXIST"]);
            Assert.Equal(1, missingDelete.ExitCode);

            var missingBackup = await RunCliAsync(vault, ["restore", Path.Combine(vault, "missing.db")]);
            Assert.Equal(1, missingBackup.ExitCode);
        }
        finally
        {
            TryDelete(vault);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "CLI")]
    public async Task Cli_config_help_and_version_are_safe_for_end_users()
    {
        var vault = CreateTempDirectory();
        var dbSecret = "Host=release-test;Database=release_db;" +
                       "User" + "name=release_user;" +
                       "Pass" + "word=release-secret-" + Guid.NewGuid().ToString("N");

        try
        {
            var setDb = await RunCliAsync(
                vault,
                ["secrets", "set", "DATABASE_CONNECTION", dbSecret, "--category", "Database"]);
            Assert.Equal(0, setDb.ExitCode);

            var config = await RunCliAsync(vault, ["config"]);
            Assert.Equal(0, config.ExitCode);
            Assert.DoesNotContain(dbSecret, config.StdOut + config.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("release-secret-", config.StdOut + config.StdErr, StringComparison.Ordinal);
            Assert.Contains("secret vault", config.StdOut, StringComparison.OrdinalIgnoreCase);

            var help = await RunCliAsync(vault, ["--help"]);
            Assert.Equal(0, help.ExitCode);
            Assert.Contains("secrets", help.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("install", help.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("migrate", help.StdOut, StringComparison.OrdinalIgnoreCase);

            var version = await RunCliAsync(vault, ["--version"]);
            Assert.Equal(0, version.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(version.StdOut));
        }
        finally
        {
            TryDelete(vault);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "CLI")]
    public async Task Cli_migrate_treats_user_connection_as_data_not_shell_code()
    {
        var vault = CreateTempDirectory();
        var marker = Path.Combine(vault, "cli-injection-marker.txt");

        try
        {
            var maliciousConnection = OperatingSystem.IsWindows()
                ? $@"invalid"" & type nul > ""{marker}"" & echo """
                : $@"invalid""; touch '{marker}'; #";

            var result = await RunCliAsync(
                vault,
                ["migrate", "--connection", maliciousConnection]);

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(marker),
                "A connection-string argument must never be able to execute an OS command.");
        }
        finally
        {
            TryDelete(vault);
        }
    }

    private async Task<string> ReadClipboardAsync()
    {
        var result = await ExecRawAsync(
            OperatingSystem.IsWindows() ? "powershell.exe" : "pbpaste",
            OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", "Get-Clipboard"]
                : Array.Empty<string>());

        Assert.Equal(0, result.ExitCode);
        return result.StdOut.TrimEnd('\r', '\n');
    }

    private async Task<string?> TryReadClipboardAsync()
    {
        if (await CommandExistsAsync("wl-paste"))
        {
            var result = await ExecRawAsync("wl-paste");
            return result.ExitCode == 0 ? result.StdOut.TrimEnd('\r', '\n') : null;
        }

        if (await CommandExistsAsync("xclip"))
        {
            var result = await ExecRawAsync("xclip", ["-selection", "clipboard", "-o"]);
            return result.ExitCode == 0 ? result.StdOut.TrimEnd('\r', '\n') : null;
        }

        if (await CommandExistsAsync("xsel"))
        {
            var result = await ExecRawAsync("xsel", ["--clipboard", "--output"]);
            return result.ExitCode == 0 ? result.StdOut.TrimEnd('\r', '\n') : null;
        }

        return null;
    }

    private async Task<bool> CommandExistsAsync(string command)
    {
        var result = OperatingSystem.IsWindows()
            ? await ExecRawAsync("where.exe", [command])
            : await ExecRawAsync("sh", ["-lc", $"command -v -- '{command.Replace("'", "'\\\"'\\\"'", StringComparison.Ordinal)}' >/dev/null 2>&1"]);

        return result.ExitCode == 0;
    }

    private async Task<ProcessResult> ExecRawAsync(
        string fileName,
        IReadOnlyList<string>? args = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (args is not null)
        {
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"Could not start command '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Xunit.Sdk.XunitException(
                $"Command exceeded the 20 second timeout: {fileName} {string.Join(" ", args ?? Array.Empty<string>())}");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private async Task<ProcessResult> RunCliAsync(
        string vaultDirectory,
        IReadOnlyList<string> args,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["FOREXBOT_VAULT_DIRECTORY"] = vaultDirectory;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "Cli"));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--");
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Could not start the CLI.");

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Xunit.Sdk.XunitException(
                $"CLI process exceeded the 90 second end-user test timeout. Args: {string.Join(" ", args)}");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "forexbot-cli-interactive-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup must not hide product failures.
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ForexTradingBot.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate ForexTradingBot.sln from the CLI E2E test output directory.");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
