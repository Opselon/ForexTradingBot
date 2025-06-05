namespace Shared.Settings
{
    /// <summary>
    /// Represents the configuration settings for integrating with the CryptoPay API.
    /// These settings are typically loaded from application configuration sources
    /// like `appsettings.json`.
    /// </summary>
    public class CryptoPaySettings
    {
        /// <summary>
        /// Gets the name of the configuration section in `appsettings.json` where
        /// CryptoPay settings are defined. This constant helps in retrieving the section
        /// programmatically, e.g., `Configuration.GetSection(CryptoPaySettings.SectionName)`.
        /// </summary>
        public const string SectionName = "CryptoPay"; // Section name in appsettings.json

        /// <summary>
        /// Gets or sets the API Token obtained from `@CryptoBot` (or `@CryptoTestnetBot` for testnet).
        /// This token is essential for authenticating requests to the CryptoPay API.
        /// It is a highly sensitive credential and should be stored securely (e.g., environment variables, Azure Key Vault).
        /// </summary>
        /// <remarks>
        /// This token is usually required for almost all CryptoPay API interactions.
        /// </remarks>
        public string ApiToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base URL for the CryptoPay API endpoints.
        /// This typically points to either the production or testnet API.
        /// <list type="bullet">
        /// <item><c>https://pay.crypt.bot/api/</c> for the production environment.</item>
        /// <item><c>https://testnet-pay.crypt.bot/api/</c> for the testnet environment.</item>
        /// </list>
        /// The default value is set to the production API URL.
        /// </summary>
        public string BaseUrl { get; set; } = "https://pay.crypt.bot/api/";

        /// <summary>
        /// Gets or sets a value indicating whether the application should use the CryptoPay testnet.
        /// If set to <see langword="true"/>, it's recommended to also configure <see cref="BaseUrl"/>
        /// to point to the testnet URL (`https://testnet-pay.crypt.bot/api/`) and use a testnet API token.
        /// Default is <see langword="false"/>.
        /// </summary>
        public bool IsTestnet { get; set; } = false;

        /// <summary>
        /// Gets or sets a secret token used to verify the authenticity of incoming
        /// webhook updates from CryptoPay. This token is configured within your
        /// CryptoPay application settings and must match the one used here.
        /// It helps protect against forged webhook requests.
        /// </summary>
        /// <remarks>
        /// This is distinct from your Telegram bot's webhook secret. This secret
        /// should also be treated as sensitive information.
        /// </remarks>
        public string? WebhookSecretForCryptoPay { get; set; }
    }
}