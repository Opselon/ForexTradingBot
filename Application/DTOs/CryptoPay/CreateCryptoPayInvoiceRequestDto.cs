using System.Text.Json.Serialization; // برای JsonPropertyName

namespace Application.DTOs.CryptoPay
{
    public class CreateCryptoPayInvoiceRequestDto
    {
        [JsonPropertyName("asset")]
        public string Asset { get; set; } // e.g., "USDT", "TON"

        [JsonPropertyName("amount")]
        public string Amount { get; set; } // به صورت رشته برای دقت بالا "125.50"

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("hidden_message")]
        public string? HiddenMessage { get; set; }

        [JsonPropertyName("paid_btn_name")]
        public string? PaidBtnName { get; set; } // "viewItem", "openChannel", "openBot", "callback"

        [JsonPropertyName("paid_btn_url")]
        public string? PaidBtnUrl { get; set; }

        [JsonPropertyName("payload")]
        public string? Payload { get; set; } // داده‌های سفارشی شما، مثلاً OrderId

        [JsonPropertyName("allow_comments")]
        public bool? AllowComments { get; set; } = true;

        [JsonPropertyName("allow_anonymous")]
        public bool? AllowAnonymous { get; set; } = true;

        [JsonPropertyName("expires_in")]
        public int? ExpiresInSeconds { get; set; } // 1-2678400

        // برای پرداخت‌های فیات
        [JsonPropertyName("currency_type")]
        public string? CurrencyType { get; set; } // "crypto" or "fiat"

        [JsonPropertyName("fiat")]
        public string? FiatCurrency { get; set; } // e.g., "USD", "EUR" (if currency_type is "fiat")

        [JsonPropertyName("accepted_assets")]
        public string? AcceptedAssets { get; set; } // Comma-separated, e.g., "USDT,TON" (if currency_type is "fiat")

    }
}