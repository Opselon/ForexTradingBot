using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Assuming this is the primary namespace for your application's interface definitions
namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that provides market data for various symbols.
    /// </summary>
    public interface IMarketDataService
    {
        /// <summary>
        /// Asynchronously retrieves market data for a given symbol.
        /// </summary>
        /// <param name="symbol">The market symbol to fetch data for (e.g., "EURUSD", "XAUUSD", "BTCUSD").</param>
        /// <param name="forceRefresh">
        /// Optional flag to indicate if any short-term cache in the service implementation should be bypassed.
        /// Defaults to false.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result contains
        /// a <see cref="MarketData"/> object with the fetched and calculated information.
        /// Returns a MarketData object with IsPriceLive=false and relevant remarks if data cannot be fetched.
        /// Should ideally not return null, but rather a MarketData object indicating failure/no data.
        /// </returns>
        Task<MarketData> GetMarketDataAsync(string symbol, bool forceRefresh = false, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the consolidated market data for a symbol, including fetched values,
    /// calculated indicators, and metadata about the data itself.
    /// This is the DTO (Data Transfer Object) used to pass market information
    /// from the MarketDataService to its consumers (e.g., command handlers).
    /// </summary>
    public class MarketData
    {
        /// <summary>
        /// The symbol for which the data was fetched (e.g., "EURUSD", "XAUUSD").
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// User-friendly name of the currency or asset (e.g., "Euro/US Dollar", "Gold (XAU/USD)").
        /// </summary>
        public string CurrencyName { get; set; } = "N/A";

        /// <summary>
        /// A brief description of the asset.
        /// </summary>
        public string Description { get; set; } = "No description available.";

        /// <summary>
        /// The current price of the asset. 0 if not available.
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// The percentage change in price over the last 24 hours.
        /// </summary>
        public decimal Change24h { get; set; } // Percentage

        /// <summary>
        /// The highest price in the last 24 hours. 0 if not available.
        /// </summary>
        public decimal High24h { get; set; }

        /// <summary>
        /// The lowest price in the last 24 hours. 0 if not available.
        /// </summary>
        public decimal Low24h { get; set; }

        /// <summary>
        /// Trading volume in the last 24 hours (typically in the quote currency or a standard like USD).
        /// 0 if not available.
        /// </summary>
        public decimal Volume { get; set; }

        /// <summary>
        /// Market capitalization. 0 if not available.
        /// </summary>
        public decimal MarketCap { get; set; }

        /// <summary>
        /// Calculated Relative Strength Index (RSI). Typically 0-100.
        /// Might be an estimation if full historical data is not available.
        /// Default to 50 (neutral) if not calculable.
        /// </summary>
        public decimal RSI { get; set; } = 50m;

        /// <summary>
        /// Moving Average Convergence Divergence (MACD) indicator summary or status.
        /// Often "N/A (Requires History)" if only spot data is available.
        /// </summary>
        public string MACD { get; set; } = "N/A";

        /// <summary>
        /// Calculated or fetched support level. 0 if not available.
        /// </summary>
        public decimal Support { get; set; }

        /// <summary>
        /// Calculated or fetched resistance level. 0 if not available.
        /// </summary>
        public decimal Resistance { get; set; }

        /// <summary>
        /// Estimated or calculated volatility, typically as a percentage.
        /// 0 if not available.
        /// </summary>
        public decimal Volatility { get; set; } // Percentage

        /// <summary>
        /// Derived short-term market trend (e.g., "Uptrend", "Downtrend", "Sideways", "N/A").
        /// </summary>
        public string Trend { get; set; } = "N/A";

        /// <summary>
        /// Derived market sentiment (e.g., "Bullish", "Bearish", "Neutral", "N/A").
        /// </summary>
        public string MarketSentiment { get; set; } = "N/A";

        /// <summary>
        /// Price change percentage over the last 7 days. 0 if not available.
        /// </summary>
        public decimal PriceChangePercentage7d { get; set; }

        /// <summary>
        /// Price change percentage over the last 30 days. 0 if not available.
        /// </summary>
        public decimal PriceChangePercentage30d { get; set; }

        /// <summary>
        /// Identifier for this asset on a proxy data provider (e.g., CoinGecko ID like "tether-gold").
        /// This is often copied from CurrencyDetails for context. Null if not applicable.
        /// </summary>
        public string? CoinGeckoId { get; set; } // Or a more generic ProxyDataProviderId

        /// <summary>
        /// The timestamp (UTC) when this data was fetched or last calculated.
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Name of the data source from which the primary price was obtained (e.g., "CoinGecko", "Frankfurter.app", "Cached").
        /// "Unavailable" or "Not Fetched" if no data could be obtained.
        /// </summary>
        public string DataSource { get; set; } = "Not Fetched";

        /// <summary>
        /// Indicates if the <see cref="Price"/> and related fields represent a live, recently fetched value.
        /// False if data is from an old cache entry or could not be fetched.
        /// </summary>
        public bool IsPriceLive { get; set; } = false;

        /// <summary>
        /// A list of important notes or remarks about the data, such as whether an indicator is estimated,
        /// if data is stale, or if a fallback source was used.
        /// </summary>
        public List<string> Remarks { get; set; }

        /// <summary>
        /// The name of the specific API provider configuration used to fetch this data successfully.
        /// Useful for debugging or if different providers have different data qualities.
        /// </summary>
        public string? ProviderNameUsed { get; set; }

        /// <summary>
        /// User-facing insights or brief analytical points derived from the data.
        /// This is separate from Remarks, which are more about data quality/source.
        /// This was the field that caused the compile error earlier.
        /// </summary>
        public List<string> Insights { get; set; }


        public MarketData()
        {
            // Initialize collections to prevent null reference exceptions.
            Remarks = new List<string>();
            Insights = new List<string>();
            // Set a sensible default LastUpdated to avoid DateTime.MinValue if an object is created but not populated.
            LastUpdated = DateTime.UtcNow;
        }
    }
}