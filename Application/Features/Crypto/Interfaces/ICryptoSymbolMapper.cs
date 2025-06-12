// -----------------
// UPDATED FILE: Application/Features/Crypto/Interfaces/ICryptoSymbolMapper.cs
// -----------------

namespace Application.Features.Crypto.Interfaces
{
    /// <summary>
    /// Defines a contract for a service that maps cryptocurrency identifiers,
    /// primarily mapping standard symbols to provider-specific IDs/symbols.
    /// </summary>
    public interface ICryptoSymbolMapper
    {
        /// <summary>
        /// Gets the CoinGecko ID corresponding to a given standard cryptocurrency symbol.
        /// </summary>
        /// <param name="symbol">The standard cryptocurrency symbol (e.g., "BTC", "ETH").</param>
        /// <returns>The corresponding CoinGecko ID (e.g., "bitcoin", "ethereum") or null if no mapping exists.</returns>
        string? GetCoinGeckoId(string symbol); // <--- ADDED THIS METHOD

        /// <summary>
        /// Gets the Financial Modeling Prep (FMP) symbol corresponding to a given standard cryptocurrency symbol.
        /// </summary>
        /// <param name="symbol">The standard cryptocurrency symbol (e.g., "BTC", "ETH").</param>
        /// <returns>The corresponding FMP symbol (e.g., "BTCUSD", "ETHUSD") or null if no mapping exists.</returns>
        // FIX: Changed parameter type from coinGeckoId to symbol to align with Orchestrator logic
        string? GetFmpSymbol(string symbol);
    }
}