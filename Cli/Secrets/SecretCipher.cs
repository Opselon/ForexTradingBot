using System.Security.Cryptography;

namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// Encrypts secret values at rest without ever writing the key to disk.
///
/// The machine key is derived from a per-installation random salt (stored
/// alongside the vault, in the user's own profile directory) combined with
/// the current OS user identity. That means:
///   * the vault file can be copied, but is useless on another machine or
///     under another user account, and
///   * there is no key file to lose or leak.
///
/// This deliberately does NOT use ASP.NET DataProtection: the CLI must work
/// standalone (no web host) and on the same machine after an app upgrade.
/// </summary>
public interface ISecretCipher
{
    string Encrypt(string plaintext, byte[] salt);
    string Decrypt(string ciphertext, byte[] salt);
}

public sealed class SecretCipher : ISecretCipher
{
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // GCM nonce
    private const int TagSize = 16;   // GCM tag

    private readonly byte[] _identity;

    public SecretCipher(byte[] machineIdentity)
    {
        _identity = machineIdentity ?? throw new ArgumentNullException(nameof(machineIdentity));
    }

    /// <summary>
    /// A stable identity for the current OS user: the SID on Windows, the UID
    /// on Unix. Combined with the per-install salt this produces the AES key.
    /// </summary>
    public static byte[] CurrentUserIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "win-user";
            return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid));
        }
        // Unix: uid + gid + host name
        var uid = Environment.UserName;
        var host = Environment.MachineName;
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{uid}@{host}"));
    }

    public string Encrypt(string plaintext, byte[] salt)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var key = DeriveKey(salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: nonce(12) || tag(16) || ciphertext
        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);

        CryptographicOperations.ZeroMemory(key);
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string ciphertext, byte[] salt)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return string.Empty;
        }

        var key = DeriveKey(salt);
        try
        {
            var data = Convert.FromBase64String(ciphertext);
            if (data.Length < NonceSize + TagSize)
            {
                throw new FormatException("Ciphertext is too short to contain nonce and tag.");
            }

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
        // HKDF over (identity || salt): re-deriving on every call keeps the
        // key out of long-lived memory, and the salt makes each installation
        // use a distinct key even on a shared machine.
        var combined = new byte[_identity.Length + salt.Length];
        Buffer.BlockCopy(_identity, 0, combined, 0, _identity.Length);
        Buffer.BlockCopy(salt, 0, combined, _identity.Length, salt.Length);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, KeySize, salt: null, info: System.Text.Encoding.UTF8.GetBytes("ForexTradingBot.SecretVault.v1"));
    }
}
