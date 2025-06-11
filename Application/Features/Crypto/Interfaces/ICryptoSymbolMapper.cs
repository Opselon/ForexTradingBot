// -----------------
// NEW FILE
// -----------------
namespace Application.Features.Crypto.Interfaces
{
    /// <summary>
    /// Defines a contract for a service that maps cryptocurrency identifiers
    /// between different API providers (e.g., CoinGecko ID to FMP Symbol).
    /// </summary>
    public interface ICryptoSymbolMapper
    {
        /// <summary>
        /// Gets the Financial Modeling Prep (FMP) symbol corresponding to a given CoinGecko ID.
        /// </summary>
        /// <param name="coinGeckoId">The unique identifier from CoinGecko (e.g., "usd-coin").</param>
        /// <returns>The corresponding FMP symbol (e.g., "USDCUSD") or null if no mapping exists.</returns>
        string? GetFmpSymbol(string coinGeckoId);
    }
}