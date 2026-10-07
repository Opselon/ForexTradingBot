namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// The categories a secret can belong to. Drives the UI grouping and the
/// "never leave the machine" guarantee: every value is encrypted at rest
/// with a key that never leaves the user's own device.
/// </summary>
public enum SecretCategory
{
    Telegram,
    CryptoPay,
    Database,
    Redis,
    Api,
    Smtp,
    Other
}

/// <summary>
/// A single stored secret. <see cref="Value"/> is only ever materialised in
/// memory, on the user's own machine, after decryption.
/// </summary>
public sealed class SecretRecord
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public SecretCategory Category { get; init; } = SecretCategory.Other;
    public string? Description { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}
