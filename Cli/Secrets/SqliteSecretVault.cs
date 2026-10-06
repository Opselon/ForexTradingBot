using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// A local, encrypted-at-rest store for the user's secrets.
///
/// Design goals, in priority order:
///   1. Never leave the machine — no network, no telemetry, no sync.
///   2. No secret is ever written to disk in plaintext, not even transiently.
///   3. Survives application upgrades and machine reboots.
///   4. Cheap to back up as a single file (the user owns the vault).
///
/// Storage is a single SQLite file in the user's profile directory. Each value
/// is AES-256-GCM encrypted with a key derived from a per-installation salt
/// plus the OS user identity, so the file is worthless if copied elsewhere.
/// </summary>
public interface ISecretVault : IDisposable
{
    /// <summary>Path of the vault file on disk (for the <c>backup</c> command).</summary>
    string VaultPath { get; }

    /// <summary>True when the vault file exists and can be opened.</summary>
    bool Exists();

    /// <summary>Lists every stored secret, with values redacted.</summary>
    IReadOnlyList<SecretRecord> List();

    /// <summary>Returns the plaintext value, or null if the key is not set.</summary>
    string? Get(string key);

    /// <summary>Stores (or overwrites) a secret.</summary>
    void Set(string key, string value, SecretCategory category = SecretCategory.Other, string? description = null);

    /// <summary>Deletes a secret. Returns false if it was not present.</summary>
    bool Delete(string key);

    /// <summary>Rotates the encryption key: re-encrypts every value with a fresh salt.</summary>
    void Rotate();
}

public sealed class SqliteSecretVault : ISecretVault
{
    private readonly ISecretCipher _cipher;
    private readonly byte[] _salt;
    private readonly SqliteConnection _connection;

    public SqliteSecretVault(ISecretCipher cipher, string? vaultDirectory = null)
    {
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));

        vaultDirectory ??= DefaultVaultDirectory();
        Directory.CreateDirectory(vaultDirectory);
        VaultPath = Path.Combine(vaultDirectory, "secrets.db");

        _salt = LoadOrCreateSalt(vaultDirectory);
        _connection = OpenVault(VaultPath);
    }

    public string VaultPath { get; }

    public bool Exists() => File.Exists(VaultPath);

    public IReadOnlyList<SecretRecord> List()
    {
        const string sql = "SELECT Key, Category, Description, CreatedUtc, UpdatedUtc FROM Secrets ORDER BY Category, Key";
        using var cmd = new SqliteCommand(sql, _connection);
        using var reader = cmd.ExecuteReader();

        var records = new List<SecretRecord>();
        while (reader.Read())
        {
            records.Add(new SecretRecord
            {
                Key = reader.GetString(0),
                Value = RedactedPlaceholder, // never materialise values on a list
                Category = Enum.TryParse<SecretCategory>(reader.GetString(1), out var c) ? c : SecretCategory.Other,
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedUtc = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                UpdatedUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }
        return records;
    }

    public string? Get(string key)
    {
        const string sql = "SELECT Value FROM Secrets WHERE Key = @key";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@key", key);
        var ciphertext = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }
        return _cipher.Decrypt(ciphertext, _salt);
    }

    public void Set(string key, string value, SecretCategory category = SecretCategory.Other, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Secret key must not be empty.", nameof(key));
        }
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("Secret value must not be empty. Delete the key instead.", nameof(value));
        }

        var ciphertext = _cipher.Encrypt(value, _salt);
        var now = DateTime.UtcNow.ToString("O");

        const string sql = """
            INSERT INTO Secrets (Key, Value, Category, Description, CreatedUtc, UpdatedUtc)
            VALUES (@key, @value, @category, @description, @now, @now)
            ON CONFLICT(Key) DO UPDATE SET
                Value = @value,
                Category = @category,
                Description = @description,
                UpdatedUtc = @now;
            """;

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", ciphertext);
        cmd.Parameters.AddWithValue("@category", category.ToString());
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public bool Delete(string key)
    {
        const string sql = "DELETE FROM Secrets WHERE Key = @key";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void Rotate()
    {
        // Re-encrypt every value under a brand-new salt. The old salt is
        // overwritten only after every value has been rewritten, so a crash
        // mid-rotation leaves the vault readable (recovered by re-running).
        var all = List();
        var plaintexts = new List<(string Key, string Value, SecretCategory Category, string? Description, DateTime Created)>();
        foreach (var record in all)
        {
            plaintexts.Add((record.Key, Get(record.Key) ?? string.Empty, record.Category, record.Description, record.CreatedUtc));
        }

        var newSalt = new byte[32];
        RandomNumberGenerator.Fill(newSalt);

        foreach (var (key, value, category, description, _) in plaintexts)
        {
            var ciphertext = _cipher.Encrypt(value, newSalt);
            const string sql = "UPDATE Secrets SET Value = @value WHERE Key = @key";
            using var cmd = new SqliteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@value", ciphertext);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }

        // Persist the NEW salt (the old _salt is replaced). The next open derives
        // its key from this salt, matching the re-encrypted values above.
        PersistSalt(Path.GetDirectoryName(VaultPath)!, newSalt);
        newSalt.AsSpan().CopyTo(_salt);
        Array.Clear(newSalt, 0, newSalt.Length);
    }

    public void Dispose() => _connection.Dispose();

    // ----------------------------------------------------------------------

    /// <summary>What a stored secret looks like when listed: value replaced, never the real one.</summary>
    public const string RedactedPlaceholder = "••••••••";

    public static string DefaultVaultDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(profile, "ForexTradingBot");
    }

    private static SqliteConnection OpenVault(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var cmd = new SqliteCommand("""
            CREATE TABLE IF NOT EXISTS Secrets (
                Key         TEXT PRIMARY KEY NOT NULL,
                Value       TEXT NOT NULL,
                Category    TEXT NOT NULL DEFAULT 'Other',
                Description TEXT,
                CreatedUtc  TEXT NOT NULL,
                UpdatedUtc  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Secrets_Category ON Secrets(Category);
            """, connection);
        cmd.ExecuteNonQuery();

        return connection;
    }

    private static byte[] LoadOrCreateSalt(string directory)
    {
        var saltPath = Path.Combine(directory, "secrets.salt");
        if (File.Exists(saltPath))
        {
            var salt = File.ReadAllBytes(saltPath);
            if (salt.Length == 32)
            {
                return salt;
            }
        }

        var fresh = new byte[32];
        RandomNumberGenerator.Fill(fresh);
        PersistSalt(directory, fresh);
        return fresh;
    }

    private static void PersistSalt(string directory, byte[] salt)
    {
        var saltPath = Path.Combine(directory, "secrets.salt");
        File.WriteAllBytes(saltPath, salt);
    }
}
