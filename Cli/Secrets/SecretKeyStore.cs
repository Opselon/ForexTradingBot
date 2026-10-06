using System.Security.Cryptography;

namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// Owns the local vault master key.
/// Windows protects the key with DPAPI scoped to the current user.
/// Unix-like systems store the random key in a user-only file.
/// </summary>
public static class SecretKeyStore
{
    private const int KeySize = 32;
    private const string KeyFileName = "secrets.key";

    public static byte[] LoadOrCreateKey(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, KeyFileName);
        if (File.Exists(path))
        {
            var stored = File.ReadAllBytes(path);
            try
            {
                var key = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                    : stored.ToArray();

                if (key.Length != KeySize)
                    throw new CryptographicException("The local secret-vault key has an invalid length.");

                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        var keyBytes = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var persisted = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(keyBytes, null, DataProtectionScope.CurrentUser)
                : keyBytes.ToArray();

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, persisted);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(
                        temp,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Some mounted filesystems do not expose Unix permission bits.
                }
            }

            File.Move(temp, path, true);
            return keyBytes.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
