// -----------------
// UPDATED FILE: Application/Features/Crypto/Services/CryptoSymbolMapper.cs
// Strong Powerful Logic Edition
// -----------------
using Application.Features.Crypto.Interfaces; // Make sure this using directive is correct
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System; // For ArgumentNullException
using System.Linq; // For Any() (optional but good practice)

namespace Application.Features.Crypto.Services // Make sure this namespace is correct
{
    /// <summary>
    /// Implements the ICryptoSymbolMapper interface, providing mappings
    /// from standard cryptocurrency symbols to provider-specific identifiers.
    /// Uses in-memory dictionaries initialized once.
    /// </summary>
    public class CryptoSymbolMapper : ICryptoSymbolMapper
    {
        private readonly ILogger<CryptoSymbolMapper> _logger;

        // Primary maps: Standard Symbol (e.g., "BTC") -> Provider ID/Symbol
        // Use case-insensitive comparer for symbol lookups
        private static readonly Dictionary<string, string> SymbolToCoinGeckoIdMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Symbol -> CoinGecko ID (These are CoinGecko's internal IDs)
            { "BTC", "bitcoin" },
            { "ETH", "ethereum" },
            { "USDT", "tether" }, // CoinGecko ID is 'tether' for USDT
            { "BNB", "binancecoin" },
            { "SOL", "solana" },
            { "XRP", "ripple" },
            // Add the WETH mapping which caused the 404
            { "WETH", "wrapped-ether" }, // Example CoinGecko ID for WETH
            { "ADA", "cardano" },
            { "DOGE", "dogecoin" },
            { "TRX", "tron" },
            { "USDC", "usd-coin" }, // CoinGecko ID is 'usd-coin'
            { "DOT", "polkadot" },
            { "MATIC", "polygon" }, // Example Polygon ID
            { "SHIB", "shiba-inu" },
            { "DAI", "dai" },
            { "BCH", "bitcoin-cash" },
            { "LINK", "chainlink" },
            { "LTC", "litecoin" },
            { "AVAX", "avalanche-2" }, // CoinGecko ID might have suffixes like '-2'
            { "UNI", "uniswap" },
            { "XMR", "monero" },
            // --- Add all symbols you intend to support via CoinGecko here ---
            // Verify CoinGecko IDs from their API documentation or explorer
        };

        private static readonly Dictionary<string, string> SymbolToFmpSymbolMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Symbol -> FMP Symbol (Adjust format like BTCUSD based on FMP's requirements)
            { "BTC", "BTCUSD" },
            { "ETH", "ETHUSD" },
            { "USDT", "USDTUSD" },
            { "BNB", "BNBUSD" },
            { "SOL", "SOLUSD" },
            { "XRP", "XRPUSD" },
            // Add WETH mapping if FMP supports it and has a specific symbol
            { "WETH", "WETHUSD" }, // Example FMP symbol for WETH - VERIFY THIS
            { "ADA", "ADAUSD" },
            { "DOGE", "DOGEUSD" },
            { "TRX", "TRXUSD" },
            { "USDC", "USDCUSD" },
            { "DOT", "DOTUSD" },
            { "MATIC", "MATICUSD" }, // Example
            { "SHIB", "SHIBUSD" },
            { "DAI", "DAIUSD" },
            { "BCH", "BCHUSD" },
            { "LINK", "LINKUSD" },
            { "LTC", "LTCUSD" },
            { "AVAX", "AVAXUSD" },
            { "UNI", "UNIUSD" },
            { "XMR", "XMRUSD" },
            // --- Add all symbols you intend to support via FMP here ---
            // Verify FMP symbols from their API documentation
        };

        // Optional: Reverse map for getting Symbol from CoinGecko ID (useful for list view if API only gives IDs)
        // private static readonly Dictionary<string, string> CoinGeckoIdToSymbolMap;

        /*
        // If you need the reverse map (CoinGecko ID -> Symbol), initialize it like this:
        static CryptoSymbolMapper()
        {
             CoinGeckoIdToSymbolMap = SymbolToCoinGeckoIdMap.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
             // Note: This assumes CoinGecko IDs are unique values in SymbolToCoinGeckoIdMap
        }
        */


        public CryptoSymbolMapper(ILogger<CryptoSymbolMapper> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogInformation("CryptoSymbolMapper initialized with {CoinGeckoMapCount} CoinGecko symbol maps and {FmpMapCount} FMP symbol maps.",
                                   SymbolToCoinGeckoIdMap.Count, SymbolToFmpSymbolMap.Count);
        }

        /// <summary>
        /// Gets the CoinGecko ID corresponding to a given standard cryptocurrency symbol.
        /// </summary>
        /// <param name="symbol">The standard cryptocurrency symbol (e.g., "BTC").</param>
        /// <returns>The corresponding CoinGecko ID (e.g., "bitcoin") or null if no mapping exists.</returns>
        public string? GetCoinGeckoId(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Attempted to map a null or whitespace symbol to CoinGecko ID.");
                return null;
            }

            if (SymbolToCoinGeckoIdMap.TryGetValue(symbol.Trim(), out var coinGeckoId))
            {
                // Use LogTrace for successful mapping as this happens for every details request
                _logger.LogTrace("Mapped Symbol '{Symbol}' to CoinGecko ID '{CoinGeckoId}'.", symbol.Trim(), coinGeckoId);
                return coinGeckoId;
            }

            _logger.LogWarning("No CoinGecko mapping found for symbol '{Symbol}'.", symbol.Trim());
            return null; // Return null to indicate no mapping exists
        }

        /// <summary>
        /// Gets the Financial Modeling Prep (FMP) symbol corresponding to a given standard cryptocurrency symbol.
        /// </summary>
        /// <param name="symbol">The standard cryptocurrency symbol (e.g., "BTC").</param>
        /// <returns>The corresponding FMP symbol (e.g., "BTCUSD") or null if no mapping exists.</returns>
        // FIX: Signature changed to accept symbol, not coinGeckoId
        public string? GetFmpSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Attempted to map a null or whitespace symbol to FMP symbol.");
                return null;
            }

            // FIX: Use the SymbolToFmpSymbolMap
            if (SymbolToFmpSymbolMap.TryGetValue(symbol.Trim(), out var fmpSymbol))
            {
                // Use LogTrace for successful mapping
                _logger.LogTrace("Mapped Symbol '{Symbol}' to FMP Symbol '{FmpSymbol}'.", symbol.Trim(), fmpSymbol);
                return fmpSymbol;
            }

            _logger.LogWarning("No FMP mapping found for symbol '{Symbol}'.", symbol.Trim());
            return null; // Return null to indicate no mapping exists
        }

        // Optional: Add a method to get the Symbol from CoinGecko ID if needed by your list fetching logic
        /*
        public string? GetSymbolFromCoinGeckoId(string coinGeckoId)
        {
             if (string.IsNullOrWhiteSpace(coinGeckoId))
            {
                 _logger.LogWarning("Attempted to map a null or whitespace CoinGecko ID to symbol.");
                 return null;
            }
            if (CoinGeckoIdToSymbolMap.TryGetValue(coinGeckoId.Trim(), out var symbol))
            {
                 _logger.LogTrace("Mapped CoinGecko ID '{CoinGeckoId}' to Symbol '{Symbol}'.", coinGeckoId.Trim(), symbol);
                 return symbol;
            }
             _logger.LogWarning("No Symbol mapping found for CoinGecko ID '{CoinGeckoId}'.", coinGeckoId.Trim());
             return null;
        }
        */
    }
}