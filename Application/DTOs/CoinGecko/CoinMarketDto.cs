// -----------------
// NEW FILE
// -----------------
using System.Text.Json.Serialization;

namespace Application.DTOs.CoinGecko
{
    /// <summary>
    /// Represents the data for a single cryptocurrency from CoinGecko's /coins/markets endpoint.
    /// This DTO is optimized for list views, containing essential market data for display.
    /// </summary>
    public class CoinMarketDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("current_price")]
        public double? CurrentPrice { get; set; }

        [JsonPropertyName("market_cap")]
        public long? MarketCap { get; set; }

        [JsonPropertyName("market_cap_rank")]
        public int? MarketCapRank { get; set; }

        [JsonPropertyName("price_change_percentage_24h")]
        public double? PriceChangePercentage24h { get; set; }
    }
}