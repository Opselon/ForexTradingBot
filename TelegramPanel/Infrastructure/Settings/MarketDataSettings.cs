using System.Collections.Generic;
using System.ComponentModel.DataAnnotations; // For potential future validation

namespace TelegramPanel.Infrastructure.Settings
{
    public class MarketDataSettings
    {
        public const string SectionName = "MarketData";



        /// <summary>
        /// Configuration for various data providers.
        /// The key of the dictionary is a unique name for the provider (e.g., "CoinGecko", "FrankfurterApp").
        /// </summary>
        public Dictionary<string, ProviderSettings> Providers { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Default order of provider preference if a specific currency doesn't define its own.
        /// Uses keys from the Providers dictionary.
        /// </summary>
        public List<string> DefaultProviderPreference { get; set; } = new();

        /// <summary>
        /// Default number of retries for API calls (can be overridden per provider).
        /// </summary>
        public int DefaultRetryCount { get; set; } = 3;

        /// <summary>
        /// Default base delay in seconds for retries (used with exponential backoff).
        /// </summary>
        public int DefaultBaseRetryDelaySeconds { get; set; } = 2; // e.g., 2s, 4s, 8s

        /// <summary>
        /// Default duration in minutes for caching successfully fetched market data in IMemoryCache.
        /// </summary>
        public int DefaultCacheDurationMinutes { get; set; } = 5;

        /// <summary>
        /// How long (in hours) stale data from IMemoryCache can be used as a fallback if a live API fetch fails.
        /// </summary>
        public int StaleCacheFallbackDurationHours { get; set; } = 3;

        /// <summary>
        /// How long (in hours) the static previous price cache entry is considered for 24h change calculation.
        /// (Relevant if MarketDataService uses its internal static cache for this).
        /// </summary>
        public int StaticPreviousPriceCacheMaxAgeHours { get; set; } = 30;
    }

    public class ProviderSettings
    {
        /// <summary>
        /// Base URL for the API provider.
        /// Example: "https://api.coingecko.com/api/v3/"
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string BaseUrl { get; set; }

        /// <summary>
        /// API Key, if required by the provider.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Name of the query parameter or header for the API Key.
        /// Common examples: "api_key", "apikey", "x-api-key", "Authorization" (for Bearer tokens).
        /// </summary>
        public string? ApiKeyParameterName { get; set; }

        /// <summary>
        /// Specifies where the API key should be placed in the HTTP request.
        /// </summary>
        public ApiKeyLocation ApiKeyLocation { get; set; } = ApiKeyLocation.QueryString;

        /// <summary>
        /// If ApiKeyLocation is Header and the key is a Bearer token, set this to true.
        /// The ApiKey should then be the token itself, without "Bearer ".
        /// </summary>
        public bool IsApiKeyBearerToken { get; set; } = false;

        /// <summary>
        /// Default timeout for HTTP requests to this provider in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Is this provider configuration currently enabled for use?
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Relative priority (lower number means higher priority) if multiple providers
        /// could serve the same request and no specific preference is set for the currency.
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// Type of assets this provider primarily serves (e.g., "Forex", "Crypto", "Metals", "Stock", "Mixed").
        /// This helps in selecting the right provider if a currency's category matches.
        /// </summary>
        public string AssetTypeFocus { get; set; } = "Mixed";

        /// <summary>
        /// Defines specific endpoints for this provider and how to parse data from them.
        /// The key could be a descriptive name for the endpoint's purpose, e.g.,
        /// "SingleCoinMarketData", "ForexLatestPair", "ForexHistoricalDate".
        /// </summary>
        public Dictionary<string, ApiEndpointConfig> Endpoints { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    public enum ApiKeyLocation
    {
        QueryString,
        Header
    }

    public class ApiEndpointConfig
    {
        /// <summary>
        /// The relative path template for this endpoint.
        /// Placeholders like {Id}, {BaseAsset}, {QuoteAsset}, {TargetAssetsCommaSeparated}, {Date_YYYY-MM-DD}
        /// will be replaced by the MarketDataService.
        /// Example: "coins/{Id}?localization=false&market_data=true"
        /// Example: "latest?from={BaseAsset}&to={QuoteAsset}"
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string PathTemplate { get; set; }

        // --- JSON Path Data Extractors ---
        // These paths specify how to extract data from the JSON response of this endpoint.
        // Uses dot notation for nested objects. Arrays can be accessed by index (e.g., "data.0.price") if needed.
        // Special placeholder {PriceCurrency} can be used if the JSON key itself depends on the target currency (e.g., for CoinGecko).
        // Special placeholder {QuoteAsset} can be used if JSON key depends on quote asset (e.g. Frankfurter)

        public string? PriceJsonPath { get; set; }                // "market_data.current_price.{PriceCurrency}" or "rates.{QuoteAsset}"
        public string? Change24hPercentJsonPath { get; set; }    // "market_data.price_change_percentage_24h_in_currency.{PriceCurrency}" or "market_data.price_change_percentage_24h"
        public string? High24hJsonPath { get; set; }              // "market_data.high_24h.{PriceCurrency}"
        public string? Low24hJsonPath { get; set; }               // "market_data.low_24h.{PriceCurrency}"
        public string? Volume24hJsonPath { get; set; }            // "market_data.total_volume.{PriceCurrency}"
        public string? MarketCapJsonPath { get; set; }            // "market_data.market_cap.{PriceCurrency}"
        public string? Change7dPercentJsonPath { get; set; }
        public string? Change30dPercentJsonPath { get; set; }
        public string? LastUpdatedTimestampJsonPath { get; set; } // Path to a field that gives a last updated timestamp from API

        /// <summary>
        /// If this endpoint returns a collection of rates (e.g., 1 USD to many currencies),
        /// this is the path to the JSON object/dictionary containing these rates.
        /// The keys of this object are expected to be currency symbols (e.g., "EUR", "GBP").
        /// Example: "rates" for Frankfurter.
        /// </summary>
        public string? AggregatedRatesObjectJsonPath { get; set; }
    }

    public class CurrencyInfoSettings
    {
        public const string SectionName = "CurrencyInfo";

        /// <summary>
        /// Dictionary of currency/symbol details. Key is the symbol (e.g., "XAUUSD", "EURUSD").
        /// Using StringComparer.OrdinalIgnoreCase for case-insensitive symbol lookups.
        /// </summary>
        public Dictionary<string, CurrencyDetails> Currencies { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    public class CurrencyDetails
    {
        /// <summary>
        /// User-friendly display name (e.g., "Gold (XAU/USD)", "Euro / US Dollar").
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string Name { get; set; }

        /// <summary>
        /// Brief description of the asset or currency pair.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Category like "Forex", "Commodity", "CryptoProxy", "Stock", "Crypto".
        /// This helps in selecting an appropriate provider if AssetTypeFocus matches.
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string Category { get; set; }

        /// <summary>
        /// For currency pairs (Forex, Crypto pairs), the base asset symbol.
        /// Example: "EUR" for EURUSD. "BTC" for BTCUSDT. "XAU" for XAUUSD (conceptually).
        /// </summary>
        public string? BaseAsset { get; set; }

        /// <summary>
        /// For currency pairs, the quote asset symbol.
        /// Example: "USD" for EURUSD. "USDT" for BTCUSDT. "USD" for XAUUSD.
        /// </summary>
        public string? QuoteAsset { get; set; }

        /// <summary>
        /// Is this symbol actively tracked/supported by the bot?
        /// </summary>
        public bool IsActive { get; set; } = true;

        // --- Provider Specific Identifiers/Settings ---
        // These allow mapping this currency to specific IDs or parameters on different providers.

        /// <summary>
        /// Identifier for this asset on CoinGecko (if applicable, e.g., "tether-gold", "bitcoin").
        /// </summary>
        public string? CoinGeckoId { get; set; }

        /// <summary>
        /// The price currency to look for in CoinGecko's response (e.g., "usd", "eur").
        /// Replaces the {PriceCurrency} placeholder in JsonPaths for CoinGecko.
        /// </summary>
        public string? CoinGeckoPriceCurrency { get; set; } = "usd";


        // You could add similar ID fields for other providers if they use specific internal IDs
        // public string? AlphaVantageSymbol { get; set; }
        // public string? BinanceSymbol { get; set; }


        /// <summary>
        /// Preferred data provider(s) for this specific currency/symbol, in order of preference.
        /// Provider names should match keys in MarketDataSettings.Providers.
        /// If empty, DefaultProviderPreference from MarketDataSettings will be used.
        /// </summary>
        public List<string> DataProviderPreference { get; set; } = new();

        /// <summary>
        /// Suggested number of decimal places for displaying the price of this asset.
        /// Used for UI formatting.
        /// </summary>
        public int? DisplayDecimalPlaces { get; set; }

        /// <summary>
        /// The specific endpoint key (from ProviderSettings.Endpoints) to use for fetching
        /// the primary/latest price for this currency from a preferred provider.
        /// This allows a provider to have multiple endpoints, and we can specify which one to use for "latest price".
        /// Example: "SingleCoinMarketData" for a CoinGecko proxied asset. "ForexLatestPair" for a Forex pair.
        /// </summary>
        public string? PreferredPriceEndpointKey { get; set; }
    }
}