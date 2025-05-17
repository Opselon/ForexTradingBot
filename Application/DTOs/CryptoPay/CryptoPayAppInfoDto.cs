using System.Text.Json.Serialization;

namespace Application.DTOs.CryptoPay
{
    public class CryptoPayAppInfoDto
    {
        [JsonPropertyName("app_id")]
        public int AppId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("payment_processing_bot_username")]
        public string? PaymentProcessingBotUsername { get; set; }
    }
}