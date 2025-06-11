// -----------------
// NEW FILE
// -----------------
using System.Text.Json.Serialization;

namespace Application.DTOs.Fmp
{
    /// <summary>
    /// Represents an asset from the FMP '/stable/actively-trading-list' endpoint.
    /// This endpoint includes stocks, ETFs, and cryptocurrencies.
    /// </summary>
    public class FmpActivelyTradedDto
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("change")]
        public decimal? Change { get; set; }

        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("changesPercentage")]
        public decimal? ChangesPercentage { get; set; }
    }
}