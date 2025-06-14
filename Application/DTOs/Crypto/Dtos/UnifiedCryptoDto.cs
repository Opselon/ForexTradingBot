// -----------------
// NEW FILE FOR THE UPGRADED FEATURE
// -----------------
namespace Application.DTOs.Crypto.Dtos
{
    /// <summary>
    /// A unified DTO that represents the merged and most complete data available
    /// for a cryptocurrency from multiple sources (CoinGecko and FMP).
    /// </summary>
    public class UnifiedCryptoDto
    {
        // Data primarily from CoinGecko (richer descriptions)
        public string Id { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public int? MarketCapRank { get; set; }

        // Data that can be from either source, with FMP as a potential override for real-time price
        public decimal? Price { get; set; }
        public decimal? Change24hPercentage { get; set; }
        public decimal? DayHigh { get; set; }
        public decimal? DayLow { get; set; }
        public long? MarketCap { get; set; }
        public long? TotalVolume { get; set; }

        // Data source tracking
        public string PriceDataSource { get; set; } = "Unavailable";
        public bool IsDataStale { get; set; } = true;
    }
}