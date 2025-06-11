// -----------------
// CORRECTED FILE
// -----------------
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Application.DTOs.CoinGecko
{
    /// <summary>
    /// Represents the detailed information for a single cryptocurrency from CoinGecko's /coins/{id} endpoint.
    /// </summary>
    public class CoinDetailsDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public Dictionary<string, string>? Description { get; set; }

        [JsonPropertyName("market_data")]
        public MarketDataDto? MarketData { get; set; }
    }

    /// <summary>
    /// Represents the market data portion of the CoinDetailsDto.
    /// --- FIX: Data types changed to 'double?' to handle scientific notation from the API ---
    /// </summary>
    public class MarketDataDto
    {
        [JsonPropertyName("current_price")]
        public Dictionary<string, double?>? CurrentPrice { get; set; }

        [JsonPropertyName("market_cap")]
        public Dictionary<string, double?>? MarketCap { get; set; }

        [JsonPropertyName("total_volume")]
        public Dictionary<string, double?>? TotalVolume { get; set; }

        [JsonPropertyName("high_24h")]
        public Dictionary<string, double?>? High24h { get; set; }

        [JsonPropertyName("low_24h")]
        public Dictionary<string, double?>? Low24h { get; set; }

        [JsonPropertyName("price_change_percentage_24h")]
        public double? PriceChangePercentage24h { get; set; } // double is fine here too for consistency
    }
}