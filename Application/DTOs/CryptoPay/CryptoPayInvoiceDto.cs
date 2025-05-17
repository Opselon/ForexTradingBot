using System.Text.Json.Serialization;

namespace Application.DTOs.CryptoPay
{
    public class CryptoPayInvoiceDto
    {
        [JsonPropertyName("invoice_id")]
        public long InvoiceId { get; set; }

        [JsonPropertyName("hash")]
        public string? Hash { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; } // "active", "paid", "expired"

        [JsonPropertyName("asset")]
        public string? Asset { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("pay_url")] // Deprecated, but might still be in older responses
        public string? PayUrl { get; set; }

        [JsonPropertyName("bot_invoice_url")]
        public string? BotInvoiceUrl { get; set; } //  لینک پرداخت جدید

        [JsonPropertyName("mini_app_invoice_url")]
        public string? MiniAppInvoiceUrl { get; set; }

        [JsonPropertyName("web_app_invoice_url")]
        public string? WebAppInvoiceUrl { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAtIso { get; set; } // ISO 8601 format

        [JsonPropertyName("paid_at")]
        public string? PaidAtIso { get; set; } // ISO 8601 format

        [JsonPropertyName("allow_comments")]
        public bool AllowComments { get; set; }

        [JsonPropertyName("allow_anonymous")]
        public bool AllowAnonymous { get; set; }

        [JsonPropertyName("expiration_date")]
        public string? ExpirationDateIso { get; set; } // ISO 8601 format

        [JsonPropertyName("paid_anonymously")]
        public bool? PaidAnonymously { get; set; }

        [JsonPropertyName("comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("hidden_message")]
        public string? HiddenMessage { get; set; }

        [JsonPropertyName("payload")]
        public string? Payload { get; set; }

        [JsonPropertyName("paid_btn_name")]
        public string? PaidBtnName { get; set; }

        [JsonPropertyName("paid_btn_url")]
        public string? PaidBtnUrl { get; set; }

        // فیلدهای مربوط به پرداخت فیات و کارمزد
        [JsonPropertyName("currency_type")]
        public string? CurrencyType { get; set; }

        [JsonPropertyName("fiat")]
        public string? Fiat { get; set; }

        [JsonPropertyName("paid_asset")]
        public string? PaidAsset { get; set; }

        [JsonPropertyName("paid_amount")]
        public string? PaidAmount { get; set; }

        [JsonPropertyName("paid_fiat_rate")]
        public string? PaidFiatRate { get; set; }

        [JsonPropertyName("accepted_assets")]
        public string? AcceptedAssets { get; set; }

        [JsonPropertyName("fee_asset")]
        public string? FeeAsset { get; set; }

        [JsonPropertyName("fee_amount")]
        public string? FeeAmount { get; set; } // در مستندات Number است، اما برای سازگاری با سایر فیلدهای amount، رشته در نظر می‌گیریم

        [JsonPropertyName("paid_usd_rate")]
        public string? PaidUsdRate { get; set; }
    }
}