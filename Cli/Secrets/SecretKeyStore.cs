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
        using var gate = AcquireExclusiveLock(path + ".lock");

        if (File.Exists(path))
            return ReadPersistedKey(path);

        var keyBytes = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var persisted = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(keyBytes, null, DataProtectionScope.CurrentUser)
                : keyBytes.ToArray();

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
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
                        // Best effort on filesystems without Unix permission support.
                    }
                }

                File.Move(temp, path, overwrite: false);
                return keyBytes.ToArray();
            }
            finally
            {
                try { File.Delete(temp); } catch { }
                CryptographicOperations.ZeroMemory(persisted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static byte[] ReadPersistedKey(string path)
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

    private static FileStream AcquireExclusiveLock(string lockPath)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }
}
