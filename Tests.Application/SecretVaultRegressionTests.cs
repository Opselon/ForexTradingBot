using System.Security.Cryptography;
using System.Text;
using ForexTradingBot.Cli.Secrets;
using Microsoft.Data.Sqlite;

namespace Tests.Application;

public sealed class SecretVaultRegressionTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly byte[] _masterKey;

    public SecretVaultRegressionTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "forexbot-vault-regression-" + Guid.NewGuid().ToString("N"));
        _masterKey = RandomNumberGenerator.GetBytes(32);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_masterKey);
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    private SqliteSecretVault CreateVault() => new(new SecretCipher(_masterKey), _vaultDir);

    private string VaultPath => Path.Combine(_vaultDir, "secrets.db");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void SecretCipher_rejects_any_master_key_size_other_than_32_bytes(int size)
    {
        var key = new byte[size];

        Assert.Throws<ArgumentException>(() => new SecretCipher(key));
    }



    [Fact]
    public void BackupTo_creates_a_consistent_self_contained_sqlite_snapshot()
    {
        var backupDir = Path.Combine(
            Path.GetTempPath(),
            "forexbot-backup-regression-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(backupDir, "backup.db");

        try
        {
            using (var vault = CreateVault())
            {
                vault.Set("A", "alpha", SecretCategory.Api, "first");
                vault.Set("B", "beta", SecretCategory.Database, "second");

                vault.BackupTo(target);

                Assert.True(File.Exists(target));

                using var sourceConnection = new SqliteConnection("Data Source=" + target);
                sourceConnection.Open();

                using var cmd = sourceConnection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Secrets";
                Assert.Equal(2L, Assert.IsType<long>(cmd.ExecuteScalar()));
            }

            // BackupTo publishes the requested file path. A real restore/open
            // must therefore consume that exact database, not assume the caller
            // backed up into a directory named "secrets.db".
            var restoreDir = Path.Combine(
                Path.GetTempPath(),
                "forexbot-backup-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(restoreDir);
            try
            {
                var restorePath = Path.Combine(restoreDir, "secrets.db");
                File.Copy(target, restorePath);

                var copyKey = _masterKey.ToArray();
                using var copiedVault = new SqliteSecretVault(
                    new SecretCipher(copyKey),
                    restoreDir);

                // The backup is self-contained: its persisted salt travels with
                // the SQLite snapshot, so it must decrypt with the same master key.
                Assert.Equal("alpha", copiedVault.Get("A"));
                Assert.Equal("beta", copiedVault.Get("B"));
                CryptographicOperations.ZeroMemory(copyKey);
            }
            finally
            {
                try { Directory.Delete(restoreDir, recursive: true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(backupDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreFrom_replaces_the_destination_atomically_and_preserves_metadata()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "forexbot-source-" + Guid.NewGuid().ToString("N"));
        var destinationDir = Path.Combine(Path.GetTempPath(), "forexbot-destination-" + Guid.NewGuid().ToString("N"));

        try
        {
            var sourceKey = RandomNumberGenerator.GetBytes(32);
            var destinationKey = sourceKey.ToArray();

            using (var source = new SqliteSecretVault(new SecretCipher(sourceKey), sourceDir))
            {
                source.Set("SOURCE", "source-value", SecretCategory.Database, "source description");
            }

            using (var destination = new SqliteSecretVault(new SecretCipher(destinationKey), destinationDir))
            {
                destination.Set("DESTINATION", "must-disappear", SecretCategory.Redis);

                using var sourceVault = new SqliteSecretVault(new SecretCipher(sourceKey), sourceDir);
                destination.RestoreFrom(sourceVault);

                Assert.Equal("source-value", destination.Get("SOURCE"));
                Assert.Null(destination.Get("DESTINATION"));

                var record = Assert.Single(destination.List());
                Assert.Equal("source description", record.Description);
                Assert.Equal(SecretCategory.Database, record.Category);
            }

            CryptographicOperations.ZeroMemory(sourceKey);
            CryptographicOperations.ZeroMemory(destinationKey);
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
            try { Directory.Delete(destinationDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreFrom_does_not_modify_destination_when_source_cannot_be_decrypted()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "forexbot-bad-source-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var source = new SqliteSecretVault(new SecretCipher(_masterKey), sourceDir))
            {
                source.Set("SOURCE", "source-value", SecretCategory.Api);
            }

            var wrongKey = RandomNumberGenerator.GetBytes(32);
            using var destination = CreateVault();
            destination.Set("DESTINATION", "must-survive", SecretCategory.Api);

            using var sourceWithWrongKey = new SqliteSecretVault(new SecretCipher(wrongKey), sourceDir);
            Assert.ThrowsAny<CryptographicException>(() => destination.RestoreFrom(sourceWithWrongKey));

            Assert.Equal("must-survive", destination.Get("DESTINATION"));
            Assert.Null(destination.Get("SOURCE"));
            CryptographicOperations.ZeroMemory(wrongKey);
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SecretCipher_owns_a_copy_of_the_master_key()
    {
        var supplied = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);

        var cipher = new SecretCipher(supplied);
        CryptographicOperations.ZeroMemory(supplied);

        var encrypted = cipher.Encrypt("ownership", salt);

        Assert.Equal("ownership", cipher.Decrypt(encrypted, salt));
    }

    [Fact]
    public void Invalid_base64_ciphertext_fails_without_falling_back_to_plaintext()
    {
        var cipher = new SecretCipher(_masterKey);
        var salt = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<FormatException>(() => cipher.Decrypt("not-base64", salt));
    }

    [Fact]
    public void Invalid_base64_vault_salt_is_rejected_loudly()
    {
        using (var vault = CreateVault())
        {
            vault.Set("K", "value", SecretCategory.Api);
        }

        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE VaultMetadata SET Value = '%%%not-base64%%%' WHERE Key = 'Salt'";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<CryptographicException>(
            () => new SqliteSecretVault(new SecretCipher(_masterKey), _vaultDir));
    }

    [Fact]
    public void Rotation_survives_close_and_reopen_with_the_same_master_key()
    {
        using (var vault = CreateVault())
        {
            vault.Set("A", "alpha", SecretCategory.Api);
            vault.Set("B", "beta", SecretCategory.Redis);
            vault.Rotate();
        }

        using var reopened = CreateVault();
        Assert.Equal("alpha", reopened.Get("A"));
        Assert.Equal("beta", reopened.Get("B"));
    }

    [Fact]
    public void Encrypting_same_plaintext_twice_never_reuses_ciphertext()
    {
        var cipher = new SecretCipher(_masterKey);
        var salt = RandomNumberGenerator.GetBytes(32);

        var first = cipher.Encrypt("same-secret", salt);
        var second = cipher.Encrypt("same-secret", salt);

        Assert.NotEqual(first, second);
        Assert.Equal("same-secret", cipher.Decrypt(first, salt));
        Assert.Equal("same-secret", cipher.Decrypt(second, salt));
    }

    [Fact]
    public void Wrong_salt_cannot_decrypt_a_secret()
    {
        var cipher = new SecretCipher(_masterKey);
        var salt = RandomNumberGenerator.GetBytes(32);
        var wrongSalt = RandomNumberGenerator.GetBytes(32);
        var ciphertext = cipher.Encrypt("salt-bound-secret", salt);

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(ciphertext, wrongSalt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(28)]
    public void Tampering_with_any_ciphertext_region_fails_authentication(int byteIndex)
    {
        var cipher = new SecretCipher(_masterKey);
        var salt = RandomNumberGenerator.GetBytes(32);
        var encoded = cipher.Encrypt("tamper-me", salt);
        var bytes = Convert.FromBase64String(encoded);

        bytes[byteIndex] ^= 0x55;

        Assert.ThrowsAny<CryptographicException>(() =>
            cipher.Decrypt(Convert.ToBase64String(bytes), salt));
    }

    [Fact]
    public void Reopening_vault_with_same_key_recovers_persisted_secret()
    {
        using (var vault = CreateVault())
        {
            vault.Set("PERSIST", "survives-reopen", SecretCategory.Api, "regression");
        }

        using var reopened = CreateVault();
        Assert.Equal("survives-reopen", reopened.Get("PERSIST"));
    }

    [Fact]
    public void Upsert_preserves_created_timestamp_and_updates_updated_timestamp()
    {
        using var vault = CreateVault();
        vault.Set("TIMED", "first", SecretCategory.Api);

        var first = Assert.Single(vault.List());

        Thread.Sleep(25);
        vault.Set("TIMED", "second", SecretCategory.Redis, "updated");

        var second = Assert.Single(vault.List());

        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.True(second.UpdatedUtc >= first.UpdatedUtc);
        Assert.Equal(SecretCategory.Redis, second.Category);
        Assert.Equal("updated", second.Description);
        Assert.Equal("second", vault.Get("TIMED"));
    }

    [Fact]
    public void Rotation_changes_both_salt_and_ciphertext_but_preserves_values()
    {
        using var vault = CreateVault();
        vault.Set("A", "alpha", SecretCategory.Api);
        vault.Set("B", "beta", SecretCategory.Database);
        vault.Dispose();

        string saltBefore;
        string valueABefore;
        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT Value FROM VaultMetadata WHERE Key='Salt'), (SELECT Value FROM Secrets WHERE Key='A')";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            saltBefore = reader.GetString(0);
            valueABefore = reader.GetString(1);
        }

        using (var rotated = CreateVault())
        {
            rotated.Rotate();
            Assert.Equal("alpha", rotated.Get("A"));
            Assert.Equal("beta", rotated.Get("B"));
        }

        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT Value FROM VaultMetadata WHERE Key='Salt'), (SELECT Value FROM Secrets WHERE Key='A')";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.NotEqual(saltBefore, reader.GetString(0));
            Assert.NotEqual(valueABefore, reader.GetString(1));
        }
    }


    [Fact]
    public void Failed_rotation_preflight_does_not_modify_any_ciphertext_or_metadata()
    {
        using (var vault = CreateVault())
        {
            vault.Set("A", "alpha", SecretCategory.Api);
            vault.Set("B", "beta", SecretCategory.Api);
        }

        string saltBefore;
        string cipherBefore;
        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT Value FROM VaultMetadata WHERE Key='Salt'), (SELECT Value FROM Secrets WHERE Key='A')";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            saltBefore = reader.GetString(0);
            cipherBefore = reader.GetString(1);
        }

        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Secrets SET Value = 'corrupted' WHERE Key = 'B'";
            cmd.ExecuteNonQuery();
        }

        using (var vault = CreateVault())
        {
            Assert.ThrowsAny<CryptographicException>(() => vault.Rotate());
        }

        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT Value FROM VaultMetadata WHERE Key='Salt'), (SELECT Value FROM Secrets WHERE Key='A')";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(saltBefore, reader.GetString(0));
            Assert.Equal(cipherBefore, reader.GetString(1));
        }
    }

    [Fact]
    public void Rotation_of_empty_vault_is_safe()
    {
        using var vault = CreateVault();

        vault.Rotate();

        Assert.Empty(vault.List());
    }


    [Fact]
    public void Secrets_added_after_rotation_use_the_new_salt()
    {
        using var vault = CreateVault();
        vault.Set("BEFORE", "alpha", SecretCategory.Api);
        vault.Rotate();
        vault.Set("AFTER", "beta", SecretCategory.Api);

        Assert.Equal("alpha", vault.Get("BEFORE"));
        Assert.Equal("beta", vault.Get("AFTER"));
        Assert.Equal(2, vault.List().Count);
    }

    [Fact]
    public void Malformed_vault_salt_is_rejected_loudly()
    {
        using (var vault = CreateVault())
        {
            vault.Set("K", "value", SecretCategory.Api);
        }

        using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE VaultMetadata SET Value = @salt WHERE Key = 'Salt'";
            cmd.Parameters.AddWithValue("@salt", Convert.ToBase64String(new byte[7]));
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<CryptographicException>(() => new SqliteSecretVault(new SecretCipher(_masterKey), _vaultDir));
    }

    [Fact]
    public void Vault_metadata_contains_salt_and_no_plaintext_sidecar()
    {
        using var vault = CreateVault();
        vault.Set("MARKER", "do-not-leak-this-marker", SecretCategory.Api);

        Assert.True(File.Exists(VaultPath));
        Assert.False(File.Exists(Path.Combine(_vaultDir, "secrets.salt")));

        using var conn = new SqliteConnection($"Data Source={VaultPath}");
        conn.Open();

        using var saltCmd = conn.CreateCommand();
        saltCmd.CommandText = "SELECT Value FROM VaultMetadata WHERE Key='Salt'";
        var salt = Assert.IsType<string>(saltCmd.ExecuteScalar());
        Assert.Equal(32, Convert.FromBase64String(salt).Length);

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(VaultPath));
        Assert.DoesNotContain("do-not-leak-this-marker", raw);
    }

    [Fact]
    public void List_is_redacted_even_when_backend_values_are_real()
    {
        using var vault = CreateVault();
        vault.Set("SECRET_ONE", "super-sensitive-value", SecretCategory.Api);

        var record = Assert.Single(vault.List());

        Assert.Equal("SECRET_ONE", record.Key);
        Assert.Equal(SqliteSecretVault.RedactedPlaceholder, record.Value);
        Assert.DoesNotContain("super-sensitive-value", record.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupTo_rejects_the_live_vault_as_destination()
    {
        using var vault = CreateVault();

        Assert.Throws<ArgumentException>(() => vault.BackupTo(vault.VaultPath));
    }

    [Fact]
    public void BackupTo_overwrites_an_existing_destination_atomically()
    {
        var target = Path.Combine(
            Path.GetTempPath(),
            "forexbot-backup-overwrite-" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            using var vault = CreateVault();
            vault.Set("A", "alpha", SecretCategory.Api);
            vault.BackupTo(target);

            var firstBytes = File.ReadAllBytes(target);
            Assert.NotEmpty(firstBytes);

            vault.Set("B", "beta", SecretCategory.Database);
            vault.BackupTo(target);

            Assert.NotEqual(Convert.ToBase64String(firstBytes), Convert.ToBase64String(File.ReadAllBytes(target)));

            var restoreDir = Path.Combine(Path.GetTempPath(), "forexbot-backup-overwrite-restore-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(restoreDir);
                File.Copy(target, Path.Combine(restoreDir, "secrets.db"));
                using var restored = new SqliteSecretVault(new SecretCipher(_masterKey.ToArray()), restoreDir);
                Assert.Equal("alpha", restored.Get("A"));
                Assert.Equal("beta", restored.Get("B"));
            }
            finally
            {
                try { Directory.Delete(restoreDir, recursive: true); } catch { }
            }
        }
        finally
        {
            try { File.Delete(target); } catch { }
        }
    }

    [Fact]
    public void Set_rejects_blank_keys_and_empty_values_without_persisting_rows()
    {
        using var vault = CreateVault();

        Assert.Throws<ArgumentException>(() => vault.Set("", "value"));
        Assert.Throws<ArgumentException>(() => vault.Set("   ", "value"));
        Assert.Throws<ArgumentException>(() => vault.Set("KEY", ""));
        Assert.Empty(vault.List());
    }

    [Fact]
    public void Set_upserts_the_same_key_instead_of_creating_duplicates()
    {
        using var vault = CreateVault();

        vault.Set("DUP", "first", SecretCategory.Api);
        vault.Set("DUP", "second", SecretCategory.Database);

        var records = vault.List();
        var record = Assert.Single(records);
        Assert.Equal("DUP", record.Key);
        Assert.Equal(SecretCategory.Database, record.Category);
        Assert.Equal("second", vault.Get("DUP"));
    }

    [Fact]
    public void Get_missing_key_returns_null_without_creating_state()
    {
        using var vault = CreateVault();

        Assert.Null(vault.Get("MISSING"));
        Assert.Empty(vault.List());
    }

    [Fact]
    public void Delete_does_not_expose_or_corrupt_other_secrets()
    {
        using var vault = CreateVault();
        vault.Set("KEEP", "keep-me", SecretCategory.Api);
        vault.Set("REMOVE", "remove-me", SecretCategory.Api);

        Assert.True(vault.Delete("REMOVE"));
        Assert.Null(vault.Get("REMOVE"));
        Assert.Equal("keep-me", vault.Get("KEEP"));
        Assert.Single(vault.List());
    }

    [Fact]
    public void BackupTo_never_publishes_a_partial_temp_file_after_validation_failure()
    {
        var target = Path.Combine(
            Path.GetTempPath(),
            "forexbot-backup-failure-" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            using var vault = CreateVault();
            vault.Set("A", "alpha", SecretCategory.Api);
            vault.Set("B", "beta", SecretCategory.Api);

            // Corrupt the live ciphertext after the vault has been initialized.
            using (var conn = new SqliteConnection($"Data Source={VaultPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Secrets SET Value = '%%%corrupt%%%' WHERE Key = 'B'";
                cmd.ExecuteNonQuery();
            }

            var failure = Record.Exception(() => vault.BackupTo(target));
            Assert.NotNull(failure);
            Assert.True(
                failure is FormatException or CryptographicException,
                $"Unexpected backup validation exception: {failure.GetType().FullName}");
            Assert.False(File.Exists(target));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".tmp-*"));
        }
        finally
        {
            try { File.Delete(target); } catch { }
        }
    }

    [Fact]
    public void RestoreFrom_empty_source_removes_existing_destination_rows()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "forexbot-empty-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var source = new SqliteSecretVault(new SecretCipher(_masterKey.ToArray()), sourceDir);
            using var destination = CreateVault();
            destination.Set("OLD", "must-disappear", SecretCategory.Api);

            destination.RestoreFrom(source);

            Assert.Empty(destination.List());
            Assert.Null(destination.Get("OLD"));
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreFrom_preserves_null_description_and_timestamps()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "forexbot-metadata-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            SecretRecord original;
            using (var source = new SqliteSecretVault(new SecretCipher(_masterKey.ToArray()), sourceDir))
            {
                source.Set("META", "value", SecretCategory.Redis);
                original = Assert.Single(source.List());
            }

            using var destination = CreateVault();
            using var sourceVault = new SqliteSecretVault(new SecretCipher(_masterKey.ToArray()), sourceDir);
            destination.RestoreFrom(sourceVault);

            var restored = Assert.Single(destination.List());
            Assert.Equal(original.Key, restored.Key);
            Assert.Null(restored.Description);
            Assert.Equal(original.CreatedUtc, restored.CreatedUtc);
            Assert.Equal(original.UpdatedUtc, restored.UpdatedUtc);
            Assert.Equal("value", destination.Get("META"));
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Ciphertext_changes_when_salt_changes_even_for_the_same_plaintext()
    {
        var cipher = new SecretCipher(_masterKey);
        var saltA = RandomNumberGenerator.GetBytes(32);
        var saltB = RandomNumberGenerator.GetBytes(32);

        var a = cipher.Encrypt("same", saltA);
        var b = cipher.Encrypt("same", saltB);

        Assert.NotEqual(a, b);
        Assert.Equal("same", cipher.Decrypt(a, saltA));
        Assert.Equal("same", cipher.Decrypt(b, saltB));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(a, saltB));
    }

    [Fact]
    public void Long_unicode_secret_round_trips_without_truncation()
    {
        using var vault = CreateVault();
        var value = string.Concat(Enumerable.Repeat("آزمایشی🔐秘密-", 2048));

        vault.Set("UNICODE_LONG", value, SecretCategory.Other);

        Assert.Equal(value, vault.Get("UNICODE_LONG"));
    }

    [Fact]
    public void List_order_is_deterministic_by_category_then_key()
    {
        using var vault = CreateVault();
        vault.Set("Z", "z", SecretCategory.Api);
        vault.Set("A", "a", SecretCategory.Database);
        vault.Set("B", "b", SecretCategory.Api);

        var records = vault.List();

        Assert.Equal(new[] { "B", "Z", "A" }, records.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Delete_only_reports_true_when_a_row_was_removed()
    {
        using var vault = CreateVault();

        Assert.False(vault.Delete("MISSING"));

        vault.Set("X", "value", SecretCategory.Other);
        Assert.True(vault.Delete("X"));
        Assert.False(vault.Delete("X"));
    }
}
