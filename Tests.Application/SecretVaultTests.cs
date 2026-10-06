using System.Text;
using ForexTradingBot.Cli.Secrets;
using Xunit;

namespace Tests.Application;

/// <summary>
/// Round-trip, encryption-at-rest and tamper-detection tests for the CLI secret vault.
/// Every test runs against a temporary vault so the real user vault is never touched.
/// </summary>
public sealed class SecretVaultTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly byte[] _masterKey;

    public SecretVaultTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), $"vault-test-{Guid.NewGuid():N}");
        _masterKey = new byte[32];
        Random.Shared.NextBytes(_masterKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDir))
        {
            Directory.Delete(_vaultDir, recursive: true);
        }
    }

    private SqliteSecretVault CreateVault() => new(new SecretCipher(_masterKey), _vaultDir);

    private static string VaultFilePath(string dir) => Path.Combine(dir, "secrets.db");

    [Fact]
    public void Set_then_Get_returns_original_value()
    {
        var vault = CreateVault();
        vault.Set("TELEGRAM_TOKEN", "abc123-secret", SecretCategory.Telegram);

        Assert.Equal("abc123-secret", vault.Get("TELEGRAM_TOKEN"));
    }

    [Fact]
    public void Get_missing_key_returns_null()
    {
        using var vault = CreateVault();
        Assert.Null(vault.Get("DOES_NOT_EXIST"));
    }

    [Fact]
    public void Set_overwrites_existing_key()
    {
        using var vault = CreateVault();
        vault.Set("KEY", "first", SecretCategory.Api);
        vault.Set("KEY", "second", SecretCategory.Api);

        Assert.Equal("second", vault.Get("KEY"));
        Assert.Single(vault.List());
    }

    [Fact]
    public void Delete_removes_key()
    {
        using var vault = CreateVault();
        vault.Set("KEY", "value", SecretCategory.Api);

        Assert.True(vault.Delete("KEY"));
        Assert.False(vault.Delete("KEY"));
        Assert.Null(vault.Get("KEY"));
    }

    [Fact]
    public void List_reports_metadata_but_never_plaintext()
    {
        using var vault = CreateVault();
        vault.Set("K1", "value-one", SecretCategory.Database);
        vault.Set("K2", "value-two", SecretCategory.Api);

        var entries = vault.List();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.DoesNotContain("value-", e.Value));
        Assert.Contains(entries, e => e.Key == "K1" && e.Category == SecretCategory.Database);
        Assert.Contains(entries, e => e.Key == "K2" && e.Category == SecretCategory.Api);
    }

    [Fact]
    public void Value_is_encrypted_at_rest()
    {
        using var vault = CreateVault();
        var secret = "plaintext-marker-42";
        vault.Set("K", secret, SecretCategory.Api);

        var asText = Encoding.UTF8.GetString(File.ReadAllBytes(VaultFilePath(_vaultDir)));
        Assert.DoesNotContain(secret, asText);
    }

    [Fact]
    public void Tampering_with_ciphertext_fails_loud()
    {
        var vault = CreateVault();
        vault.Set("K", "value", SecretCategory.Api);
        vault.Dispose();

        // Corrupt the stored ciphertext directly: open the raw db, flip the value.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={VaultFilePath(_vaultDir)}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Secrets SET Value = 'garbage-not-a-ciphertext'";
            cmd.ExecuteNonQuery();
        }

        var reopened = new SqliteSecretVault(new SecretCipher(_masterKey), _vaultDir);
        Assert.ThrowsAny<Exception>(() => reopened.Get("K"));
    }

    [Fact]
    public void Different_master_key_cannot_decrypt()
    {
        using var vault = CreateVault();
        vault.Set("K", "value", SecretCategory.Api);

        var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var attacker = new SqliteSecretVault(new SecretCipher(otherKey), _vaultDir);
        Assert.ThrowsAny<Exception>(() => attacker.Get("K"));
    }

    [Fact]
    public void Rotate_reencrypts_all_secrets()
    {
        using var vault = CreateVault();
        vault.Set("A", "alpha", SecretCategory.Api);
        vault.Set("B", "beta", SecretCategory.Telegram);

        vault.Rotate();

        Assert.Equal("alpha", vault.Get("A"));
        Assert.Equal("beta", vault.Get("B"));
    }

    [Fact]
    public void Unicode_and_multiline_secrets_round_trip()
    {
        using var vault = CreateVault();
        var tricky = "سلام 🌍 line1\nline2\ttabbed";
        vault.Set("UNICODE", tricky, SecretCategory.Other);

        Assert.Equal(tricky, vault.Get("UNICODE"));
    }

    [Fact]
    public void Empty_key_or_value_is_rejected()
    {
        using var vault = CreateVault();
        Assert.Throws<ArgumentException>(() => vault.Set("", "v", SecretCategory.Api));
        Assert.Throws<ArgumentException>(() => vault.Set("K", "", SecretCategory.Api));
    }

    [Fact]
    public void Cipher_round_trip_with_salt()
    {
        var cipher = new SecretCipher(_masterKey);
        var salt = new byte[16];
        Random.Shared.NextBytes(salt);

        var ciphertext = cipher.Encrypt("round-trip", salt);
        Assert.NotEqual("round-trip", ciphertext);
        Assert.Equal("round-trip", cipher.Decrypt(ciphertext, salt));
    }

    [Fact]
    public void Cipher_output_depends_on_salt()
    {
        var cipher = new SecretCipher(_identity);
        var salt1 = new byte[16];
        var salt2 = new byte[16];
        Random.Shared.NextBytes(salt1);
        Random.Shared.NextBytes(salt2);

        var c1 = cipher.Encrypt("same plaintext", salt1);
        var c2 = cipher.Encrypt("same plaintext", salt2);
        Assert.NotEqual(c1, c2);
        Assert.Equal("same plaintext", cipher.Decrypt(c1, salt1));
        Assert.Equal("same plaintext", cipher.Decrypt(c2, salt2));
    }
}
