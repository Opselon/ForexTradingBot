// -----------------
// NEW FILE
// -----------------
using Application.Features.Crypto.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Application.Features.Crypto.Services
{
    /// <summary>
    /// Implements the ICryptoSymbolMapper interface, providing a centralized,
    /// easily updatable dictionary for symbol translations.
    /// </summary>
    public class CryptoSymbolMapper : ICryptoSymbolMapper
    {
        private readonly ILogger<CryptoSymbolMapper> _logger;

        // The single source of truth for all symbol mappings.
        private static readonly Dictionary<string, string> CoinGeckoToFmpMap = new()
        {
            // CoinGecko ID -> FMP Symbol
            { "bitcoin", "BTC" },
            { "ethereum", "ETHUSD" },
            { "tether", "USDTUSD" },
            { "binancecoin", "BNBUSD" },
            { "solana", "SOLUSD" },
            { "ripple", "XRPUSD" },
            { "staked-ether", "ETHUSD" },
            { "cardano", "ADAUSD" },
            { "dogecoin", "DOGEUSD" },
            { "tron", "TRXUSD" },
            { "usd-coin", "USDCUSD" }, // <-- The critical fix for your log
            { "chainlink", "LINKUSD" },
            { "avalanche-2", "AVAXUSD" },
            { "shiba-inu", "SHIBUSD" },
            { "polkadot", "DOTUSD" },
            { "litecoin", "LTCUSD" },
            { "bitcoin-cash", "BCHUSD" }
        };

        public CryptoSymbolMapper(ILogger<CryptoSymbolMapper> logger)
        {
            _logger = logger;
        }

        public string? GetFmpSymbol(string coinGeckoId)
        {
            if (CoinGeckoToFmpMap.TryGetValue(coinGeckoId.ToLower(), out var fmpSymbol))
            {
                _logger.LogInformation("Mapped CoinGecko ID '{CoinGeckoId}' to FMP Symbol '{FmpSymbol}'", coinGeckoId, fmpSymbol);
                return fmpSymbol;
            }

            _logger.LogWarning("No explicit map found for CoinGecko ID '{CoinGeckoId}'. No fallback will be attempted.", coinGeckoId);
            return null; // Return null to indicate no mapping exists, preventing guesses.
        }
    }
}