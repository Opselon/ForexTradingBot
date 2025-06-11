// -----------------
// NEW FILE
// -----------------
using System.Text.Json.Serialization;

namespace Application.DTOs.CoinGecko
{
    /// <summary>
    /// Represents the top-level object for a trending coin from CoinGecko's /search/trending endpoint.
    /// The API returns an object containing an 'item' property.
    /// </summary>
    public class TrendingCoinResult
    {
        [JsonPropertyName("item")]
        public TrendingCoinDto? Item { get; set; }
    }

    /// <summary>
    /// Represents the core data of a single trending cryptocurrency as returned by the CoinGecko API.
    /// This DTO is designed to be lightweight for list views.
    /// </summary>
    public class TrendingCoinDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("market_cap_rank")]
        public int? MarketCapRank { get; set; }
    }
}