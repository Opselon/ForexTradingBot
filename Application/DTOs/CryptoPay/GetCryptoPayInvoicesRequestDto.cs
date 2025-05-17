using System.Text.Json.Serialization;

namespace Application.DTOs.CryptoPay
{
    public class GetCryptoPayInvoicesRequestDto
    {
        [JsonPropertyName("asset")]
        public string? Asset { get; set; }

        [JsonPropertyName("invoice_ids")]
        public string? InvoiceIds { get; set; } // Comma-separated

        [JsonPropertyName("status")]
        public string? Status { get; set; } // "active", "paid"

        [JsonPropertyName("offset")]
        public int? Offset { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; } // 1-1000, defaults to 100
    }
}