// File: Application/DTOs/CryptoPay/CryptoPayWebhookUpdateDto.cs
#region Usings
using System.Text.Json.Serialization;
#endregion

namespace Application.DTOs.CryptoPay // ✅ Namespace صحیح
{
    public class CryptoPayWebhookUpdateDto // ✅ تغییر نام برای هماهنگی
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("update_type")]
        public string? UpdateType { get; set; }

        [JsonPropertyName("request_date")]
        public string? RequestDateIso { get; set; }

        [JsonPropertyName("payload")]
        public CryptoPayInvoiceDto? Payload { get; set; } //  از DTO موجود استفاده می‌کند
    }
}