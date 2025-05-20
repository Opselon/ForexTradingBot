using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization; // برای JsonPropertyName

namespace Application.DTOs.CryptoPay
{
    /// <summary>
    /// Data Transfer Object for creating an invoice via CryptoPay API.
    /// </summary>
    public class CreateCryptoPayInvoiceRequestDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the asset code for the invoice (e.g., "USDT", "TON").
        /// </summary>
        [Required(ErrorMessage = "Asset is required.")]
        [JsonPropertyName("asset")]
        public string Asset { get; set; } = string.Empty; // e.g., "USDT", "TON"

        /// <summary>
        /// Gets or sets the amount for the invoice, as a string for precision (e.g., "125.50").
        /// </summary>
        [Required(ErrorMessage = "Amount is required.")]
        [JsonPropertyName("amount")]
        public string Amount { get; set; } = string.Empty; // Represented as a string for high precision, e.g., "125.50"

        /// <summary>
        /// Gets or sets an optional description for the invoice.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets an optional hidden message for the invoice.
        /// </summary>
        [JsonPropertyName("hidden_message")]
        public string? HiddenMessage { get; set; }

        /// <summary>
        /// Gets or sets the name of the button to be shown to the user after payment.
        /// Valid values: "viewItem", "openChannel", "openBot", "callback".
        /// </summary>
        [JsonPropertyName("paid_btn_name")]
        public string? PaidBtnName { get; set; } // "viewItem", "openChannel", "openBot", "callback"

        /// <summary>
        /// Gets or sets the URL to be opened when the button specified in paid_btn_name is pressed.
        /// Required if paid_btn_name is set.
        /// </summary>
        [JsonPropertyName("paid_btn_url")]
        public string? PaidBtnUrl { get; set; }

        /// <summary>
        /// Gets or sets custom data to be passed back with the webhook update (e.g., OrderId).
        /// </summary>
        [JsonPropertyName("payload")]
        public string? Payload { get; set; } // Custom data, e.g., OrderId

        /// <summary>
        /// Gets or sets a value indicating whether to allow comments for the invoice. Defaults to true.
        /// </summary>
        [JsonPropertyName("allow_comments")]
        public bool? AllowComments { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to allow anonymous payments. Defaults to true.
        /// </summary>
        [JsonPropertyName("allow_anonymous")]
        public bool? AllowAnonymous { get; set; } = true;

        /// <summary>
        /// Gets or sets the expiration period for the invoice in seconds (range: 1-2678400).
        /// </summary>
        [JsonPropertyName("expires_in")]
        public int? ExpiresInSeconds { get; set; } // 1-2678400

        // For fiat payments
        /// <summary>
        /// Gets or sets the type of currency for the invoice ("crypto" or "fiat").
        /// </summary>
        [JsonPropertyName("currency_type")]
        public string? CurrencyType { get; set; } // "crypto" or "fiat"

        /// <summary>
        /// Gets or sets the fiat currency code (e.g., "USD", "EUR") if currency_type is "fiat".
        /// </summary>
        [JsonPropertyName("fiat")]
        public string? FiatCurrency { get; set; } // e.g., "USD", "EUR" (if currency_type is "fiat")

        /// <summary>
        /// Gets or sets a comma-separated list of accepted assets (e.g., "USDT,TON") if currency_type is "fiat".
        /// </summary>
        [JsonPropertyName("accepted_assets")]
        public string? AcceptedAssets { get; set; } // Comma-separated, e.g., "USDT,TON" (if currency_type is "fiat")

        #endregion
    }
}