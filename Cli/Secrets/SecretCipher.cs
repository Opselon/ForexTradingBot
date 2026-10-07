using System.Security.Cryptography;

namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// AES-256-GCM encryption for secret values.
/// The caller supplies a random local master key; predictable machine/user
/// identifiers are never used as encryption keys.
/// </summary>
public interface ISecretCipher
{
    string Encrypt(string plaintext, byte[] salt);
    string Decrypt(string ciphertext, byte[] salt);
}

public sealed class SecretCipher : ISecretCipher
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _masterKey;

    public SecretCipher(byte[] masterKey)
    {
        if (masterKey is null || masterKey.Length != KeySize)
            throw new ArgumentException("The vault master key must be exactly 32 bytes.", nameof(masterKey));

        _masterKey = masterKey.ToArray();
    }

    public string Encrypt(string plaintext, byte[] salt)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        var key = DeriveKey(salt);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];

            using var gcm = new AesGcm(key, TagSize);
            gcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            var output = new byte[NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
            return Convert.ToBase64String(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Decrypt(string ciphertext, byte[] salt)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return string.Empty;

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
        ArgumentNullException.ThrowIfNull(salt);
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _masterKey,
            KeySize,
            salt,
            System.Text.Encoding.UTF8.GetBytes("ForexTradingBot.SecretVault.v2"));
    }
}
