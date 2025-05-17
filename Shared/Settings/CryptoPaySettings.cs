namespace Shared.Settings
{
    public class CryptoPaySettings
    {
        public const string SectionName = "CryptoPay"; // نام بخش در appsettings.json

        public string ApiToken { get; set; } = string.Empty; // توکن API شما از @CryptoBot
        public string BaseUrl { get; set; } = "https://pay.crypt.bot/api/"; // یا https://testnet-pay.crypt.bot/api/ برای تست
        public bool IsTestnet { get; set; } = false; // برای انتخاب BaseUrl مناسب
        public string? WebhookSecretForCryptoPay { get; set; } // یک راز برای Webhook های دریافتی از CryptoPay (جدا از راز Webhook ربات تلگرام)
        // برای اعتبارسنجی امضای درخواست Webhook از CryptoPay
    }
}