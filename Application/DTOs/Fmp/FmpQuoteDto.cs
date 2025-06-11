// -----------------
// NEW FILE
// -----------------
using System.Text.Json.Serialization;

namespace Application.DTOs.Fmp
{
    /// <summary>
    /// Data Transfer Object (DTO) to represent the quote information for a cryptocurrency
    /// or stock as returned by the Financial Modeling Prep (FMP) API.
    /// This is kept separate from other API DTOs.
    /// </summary>
    public class FmpQuoteDto
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("changesPercentage")]
        public decimal? ChangesPercentage { get; set; }

        [JsonPropertyName("change")]
        public decimal? Change { get; set; }

        [JsonPropertyName("dayLow")]
        public decimal? DayLow { get; set; }

        [JsonPropertyName("dayHigh")]
        public decimal? DayHigh { get; set; }

        [JsonPropertyName("yearHigh")]
        public decimal? YearHigh { get; set; }

        [JsonPropertyName("yearLow")]
        public decimal? YearLow { get; set; }

        [JsonPropertyName("marketCap")]
        public long? MarketCap { get; set; }

        [JsonPropertyName("priceAvg50")]
        public decimal? PriceAvg50 { get; set; }

        [JsonPropertyName("priceAvg200")]
        public decimal? PriceAvg200 { get; set; }

        [JsonPropertyName("volume")]
        public long? Volume { get; set; }

        [JsonPropertyName("avgVolume")]
        public long? AvgVolume { get; set; }

        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; set; }
    }
}