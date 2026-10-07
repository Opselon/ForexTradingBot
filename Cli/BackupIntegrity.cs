using Microsoft.Data.Sqlite;

namespace ForexTradingBot.Cli;

/// <summary>
/// Cheap pre-flight checks for a secrets-vault backup file.
/// <see cref="SqliteSecretVault"/> restores by opening the staged copy and decrypting
/// every row, which already rejects most tampering. But a damaged SQLite file can
/// still open cleanly as an empty (or half-populated) database, and silently overwriting
/// a good vault with that would be a data-loss bug. These checks reject anything that
/// is not a structurally sound vault image before the live vault is touched.
/// </summary>
internal static class BackupIntegrity
{
    private const int SqliteHeaderMagicLength = 16;

    /// <summary>
    /// True only when the file looks like a SQLite database, <c>PRAGMA integrity_check</c>
    /// reports it as sound, and the vault's own schema tables are present.
    /// </summary>
    public static bool IsReadableSecretsDatabase(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // Smallest sane SQLite image: 100-byte header plus at least one page.
        var info = new FileInfo(path);
        if (info.Length < 1024)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[SqliteHeaderMagicLength];
            if (stream.Read(magic) != SqliteHeaderMagicLength)
            {
                return false;
            }

            if (!magic.StartsWith("SQLite format 3"u8))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();

            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = integrity.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                return false;
            }

            // A valid backup must carry the vault's own table; an empty or alien
            // database is not something we should ever restore over live secrets.
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Secrets';";
            return schema.ExecuteScalar() is long count && count > 0;
        }
        catch
        {
            return false;
        }
    }
}
