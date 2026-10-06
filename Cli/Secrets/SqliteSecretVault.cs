using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// Local encrypted-at-rest secret store.
///
/// The SQLite database is self-contained: the active vault salt is stored in
/// VaultMetadata so backups do not depend on a sidecar salt file. New vaults
/// use a random per-user master key. A legacy decryptor is retained only to
/// migrate vaults created by pre-v2 releases.
/// </summary>
public interface ISecretVault : IDisposable
{
    string VaultPath { get; }
    bool Exists();
    IReadOnlyList<SecretRecord> List();
    string? Get(string key);
    void Set(string key, string value, SecretCategory category = SecretCategory.Other, string? description = null);
    bool Delete(string key);
    void Rotate();
    void RestoreFrom(ISecretVault source);
    void BackupTo(string targetPath);
}

public sealed class SqliteSecretVault : ISecretVault
{
    private readonly ISecretCipher _cipher;
    private readonly ISecretCipher _legacyCipher;
    private readonly byte[] _salt;
    private readonly SqliteConnection _connection;

    public SqliteSecretVault(ISecretCipher cipher, string? vaultDirectory = null)
    {
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));

        vaultDirectory ??= DefaultVaultDirectory();
        Directory.CreateDirectory(vaultDirectory);
        VaultPath = Path.Combine(vaultDirectory, "secrets.db");

        _connection = OpenVault(VaultPath);
        _salt = LoadOrCreateSalt(_connection, vaultDirectory);

        // Compatibility only. New writes never use this cipher.
        _legacyCipher = new LegacySecretCipher(LegacySecretCipher.CurrentUserIdentity());
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
                Value = RedactedPlaceholder,
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
            return null;

        return DecryptWithMigrationSupport(ciphertext);
    }

    public void Set(string key, string value, SecretCategory category = SecretCategory.Other, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Secret key must not be empty.", nameof(key));

        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Secret value must not be empty. Delete the key instead.", nameof(value));

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
        var records = new List<(string Key, string Value)>();
        const string selectSql = "SELECT Key, Value FROM Secrets ORDER BY Key";
        using (var select = new SqliteCommand(selectSql, _connection))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var ciphertext = reader.GetString(1);
                records.Add((key, DecryptWithMigrationSupport(ciphertext)));
            }
        }

        var newSalt = RandomNumberGenerator.GetBytes(32);

        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var record in records)
            {
                using var update = new SqliteCommand(
                    "UPDATE Secrets SET Value = @value, UpdatedUtc = UpdatedUtc WHERE Key = @key",
                    _connection,
                    transaction);
                update.Parameters.AddWithValue("@value", _cipher.Encrypt(record.Value, newSalt));
                update.Parameters.AddWithValue("@key", record.Key);
                update.ExecuteNonQuery();
            }

            using var metadata = new SqliteCommand(
                """
                INSERT INTO VaultMetadata (Key, Value)
                VALUES ('Salt', @value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """,
                _connection,
                transaction);
            metadata.Parameters.AddWithValue("@value", Convert.ToBase64String(newSalt));
            metadata.ExecuteNonQuery();

            transaction.Commit();
            Buffer.BlockCopy(newSalt, 0, _salt, 0, _salt.Length);
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newSalt);
        }
    }

    public void BackupTo(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var destination = Path.GetFullPath(targetPath);
        var source = Path.GetFullPath(VaultPath);
        if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup destination must differ from the live vault.", nameof(targetPath));

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var backupConnection = new SqliteConnection("Data Source=" + temp);
            backupConnection.Open();

            // Ensure pending SQLite pages are durable before taking the snapshot.
            using (var checkpoint = _connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(FULL);";
                checkpoint.ExecuteNonQuery();
            }

            _connection.BackupDatabase(backupConnection);

            // The salt is authoritative state for the encrypted values. Write
            // it explicitly into the snapshot before publishing the file, then
            // validate every ciphertext while the snapshot is still staged.
            using (var metadata = backupConnection.CreateCommand())
            {
                metadata.CommandText = """
                    INSERT INTO VaultMetadata (Key, Value)
                    VALUES ('Salt', $salt)
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                    """;
                metadata.Parameters.AddWithValue("$salt", Convert.ToBase64String(_salt));
                metadata.ExecuteNonQuery();
            }

            using (var validation = backupConnection.CreateCommand())
            {
                validation.CommandText = "SELECT Value FROM Secrets ORDER BY Key";
                using var reader = validation.ExecuteReader();
                while (reader.Read())
                {
                    var ciphertext = reader.GetString(0);
                    _ = _cipher.Decrypt(ciphertext, _salt);
                }
            }

            backupConnection.Close();
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    public void RestoreFrom(ISecretVault source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Fully decrypt the source before touching the destination. This
        // guarantees a corrupt/wrong-key backup cannot partially overwrite the
        // live vault.
        var snapshot = source.List()
            .Select(record =>
            {
                var value = source.Get(record.Key)
                    ?? throw new CryptographicException(
                        $"Backup secret '{record.Key}' could not be decrypted.");
                return (record, value);
            })
            .ToList();

        using var transaction = _connection.BeginTransaction();
        try
        {
            using (var delete = new SqliteCommand("DELETE FROM Secrets", _connection, transaction))
            {
                delete.ExecuteNonQuery();
            }

            const string insertSql = """
                INSERT INTO Secrets
                    (Key, Value, Category, Description, CreatedUtc, UpdatedUtc)
                VALUES
                    (@key, @value, @category, @description, @createdUtc, @updatedUtc);
                """;

            foreach (var item in snapshot)
            {
                using var insert = new SqliteCommand(insertSql, _connection, transaction);
                insert.Parameters.AddWithValue("@key", item.record.Key);
                insert.Parameters.AddWithValue("@value", _cipher.Encrypt(item.value, _salt));
                insert.Parameters.AddWithValue("@category", item.record.Category.ToString());
                insert.Parameters.AddWithValue("@description", (object?)item.record.Description ?? DBNull.Value);
                insert.Parameters.AddWithValue("@createdUtc", item.record.CreatedUtc.ToString("O"));
                insert.Parameters.AddWithValue("@updatedUtc", item.record.UpdatedUtc.ToString("O"));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    public const string RedactedPlaceholder = "••••••••";

    public static string DefaultVaultDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile))
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var primary = Path.Combine(profile, "ForexTradingBot");

        // In a container the content root (/app) is read-only at runtime, so the
        // LocalApplicationData path (which resolves there) cannot hold the key file.
        // Fall back to a directory the image marks as writable instead of crashing.
        return IsWritable(primary) ? primary : ContainerVaultDirectory();
    }

    private static bool IsWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            using (File.OpenWrite(probe)) { }
            try { File.Delete(probe); } catch { /* probe cleanup is best-effort */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ContainerVaultDirectory()
    {
        // Matches the writable volume the Dockerfile provisions for runtime state.
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dataDir); } catch { /* created lazily below */ }
        return dataDir;
    }

    private string DecryptWithMigrationSupport(string ciphertext)
    {
        try
        {
            return _cipher.Decrypt(ciphertext, _salt);
        }
        catch (Exception currentFailure) when (
            currentFailure is CryptographicException or FormatException)
        {
            try
            {
                return _legacyCipher.Decrypt(ciphertext, _salt);
            }
            catch (Exception legacyFailure) when (
                legacyFailure is CryptographicException or FormatException)
            {
                throw new CryptographicException(
                    "The secret vault could not decrypt the stored value. The vault key, user identity, or vault data may have changed.",
                    new AggregateException(currentFailure, legacyFailure));
            }
        }
    }

    private static SqliteConnection OpenVault(string path)
    {
        var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();

        using var cmd = new SqliteCommand(
            """
            CREATE TABLE IF NOT EXISTS Secrets (
                Key         TEXT PRIMARY KEY NOT NULL,
                Value       TEXT NOT NULL,
                Category    TEXT NOT NULL DEFAULT 'Other',
                Description TEXT,
                CreatedUtc  TEXT NOT NULL,
                UpdatedUtc  TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Secrets_Category ON Secrets(Category);

            CREATE TABLE IF NOT EXISTS VaultMetadata (
                Key   TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL
            );
            """,
            connection);
        cmd.ExecuteNonQuery();

        return connection;
    }

    private static byte[] LoadOrCreateSalt(SqliteConnection connection, string directory)
    {
        using (var metadata = new SqliteCommand(
            "SELECT Value FROM VaultMetadata WHERE Key = 'Salt'",
            connection))
        {
            var stored = metadata.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(stored))
            {
                try
                {
                    var salt = Convert.FromBase64String(stored);
                    if (salt.Length == 32)
                        return salt;
                }
                catch (FormatException)
                {
                    throw new CryptographicException("The vault metadata contains an invalid salt.");
                }

                throw new CryptographicException("The vault metadata salt has an invalid length.");
            }
        }

        var legacySaltPath = Path.Combine(directory, "secrets.salt");
        byte[] fresh;
        if (File.Exists(legacySaltPath))
        {
            fresh = File.ReadAllBytes(legacySaltPath);
            if (fresh.Length != 32)
            {
                CryptographicOperations.ZeroMemory(fresh);
                throw new CryptographicException("The legacy vault salt has an invalid length.");
            }
        }
        else
        {
            fresh = RandomNumberGenerator.GetBytes(32);
        }

        try
        {
            using var metadata = new SqliteCommand(
                """
                INSERT INTO VaultMetadata (Key, Value)
                VALUES ('Salt', @value)
                ON CONFLICT(Key) DO NOTHING;
                """,
                connection);
            metadata.Parameters.AddWithValue("@value", Convert.ToBase64String(fresh));
            metadata.ExecuteNonQuery();
            return fresh.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fresh);
        }
    }
}

/// <summary>
/// Exact v1-compatible decryptor retained solely for one-way migration.
/// Never use this class for new encryption.
/// </summary>
internal sealed class LegacySecretCipher : ISecretCipher
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _identity;

    public LegacySecretCipher(byte[] identity)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public static byte[] CurrentUserIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "win-user";
            return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid));
        }

        var identity = Environment.UserName + "@" + Environment.MachineName;
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
    }

    public string Encrypt(string plaintext, byte[] salt) =>
        throw new NotSupportedException("Legacy vault encryption is migration-only.");

    public string Decrypt(string ciphertext, byte[] salt)
    {
        var key = DeriveKey(salt);
        try
        {
            var data = Convert.FromBase64String(ciphertext);
            if (data.Length < NonceSize + TagSize)
                throw new FormatException("Ciphertext is too short to contain nonce and tag.");

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var ciphertextBytes = new byte[data.Length - NonceSize - TagSize];
            Buffer.BlockCopy(data, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(data, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(data, NonceSize + TagSize, ciphertextBytes, 0, ciphertextBytes.Length);

            var plaintext = new byte[ciphertextBytes.Length];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, ciphertextBytes, tag, plaintext);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] DeriveKey(byte[] salt)
    {
        var combined = new byte[_identity.Length + salt.Length];
        Buffer.BlockCopy(_identity, 0, combined, 0, _identity.Length);
        Buffer.BlockCopy(salt, 0, combined, _identity.Length, salt.Length);
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                combined,
                KeySize,
                salt: null,
                info: System.Text.Encoding.UTF8.GetBytes("ForexTradingBot.SecretVault.v1"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }
}
