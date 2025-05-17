using System.Text.Json.Serialization;

namespace Application.DTOs.CryptoPay
{
    public class CryptoPayBalanceDto
    {
        [JsonPropertyName("currency_code")]
        public string? CurrencyCode { get; set; }

        [JsonPropertyName("available")]
        public string? Available { get; set; }

        [JsonPropertyName("onhold")]
        public string? Onhold { get; set; }
    }
}