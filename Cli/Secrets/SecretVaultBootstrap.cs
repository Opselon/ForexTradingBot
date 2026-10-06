using Microsoft.Extensions.Configuration;

namespace ForexTradingBot.Cli.Secrets;

/// <summary>
/// Loads persisted local secrets into configuration memory for the current
/// process. Persistence remains inside the local encrypted vault.
/// </summary>
public static class SecretVaultBootstrap
{
    private static readonly IReadOnlyDictionary<string, string> KeyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADMIN_PASSWORD"] = "Admin:Password",
            ["TELEGRAM_BOT_TOKEN"] = "TelegramPanel:BotToken",
            ["TELEGRAM_WEBHOOK_ADDRESS"] = "TelegramPanel:WebhookAddress",
            ["TELEGRAM_WEBHOOK_SECRET"] = "TelegramPanel:WebhookSecretToken",
            ["TELEGRAM_API_ID"] = "TelegramUserApi:ApiId",
            ["TELEGRAM_API_HASH"] = "TelegramUserApi:ApiHash",
            ["TELEGRAM_PHONE_NUMBER"] = "TelegramUserApi:PhoneNumber",
            ["TELEGRAM_FORWARDER_BOT_TOKEN"] = "TelegramUserApi:BotToken",
            ["CRYPTO_PAY_API_TOKEN"] = "CryptoPay:ApiToken",
            ["CRYPTO_PAY_WEBHOOK_SECRET"] = "CryptoPay:WebhookSecretForCryptoPay",
            ["REDIS_CONNECTION"] = "ConnectionStrings:Redis",
            ["DATABASE_PROVIDER"] = "DatabaseSettings:DatabaseProvider",
            ["DATABASE_CONNECTION"] = "ConnectionStrings:DefaultConnection",
        };

    public static void Apply(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using var vault = Open();

        foreach (var pair in KeyMap)
        {
            var value = vault.Get(pair.Key);
            if (!string.IsNullOrEmpty(value))
                configuration[pair.Value] = value;
        }
    }

    public static void Set(string key, string value)
    {
        using var vault = Open();
        vault.Set(key, value);
    }

    private static ISecretVault Open()
    {
        var directory = SqliteSecretVault.DefaultVaultDirectory();
        var key = SecretKeyStore.LoadOrCreateKey(directory);
        return new SqliteSecretVault(new SecretCipher(key), directory);
    }
}
