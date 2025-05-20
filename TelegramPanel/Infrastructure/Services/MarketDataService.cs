using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Settings;

namespace TelegramPanel.Infrastructure.Services
{
    public class MarketDataService : IMarketDataService
    {
        private readonly ILogger<MarketDataService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CurrencyInfoSettings _currencySettings;
        private readonly MarketDataSettings _marketDataSettings;
        private delegate Task<(decimal? Price, decimal? Change24hPercent, decimal? High24h, decimal? Low24h, decimal? Volume, string? DataSourceName, JsonElement? RawResponse)> ApiFetchStrategy(
            HttpClient client, string symbol, string baseAsset, string quoteAsset, CurrencyDetails currencyInfo, ILogger logger, bool forceRefresh, CancellationToken cancellationToken);

        private static readonly List<ApiFetchStrategy> _apiFetchStrategies = new List<ApiFetchStrategy>
        {
            // Prioritize APIs that might give more complete data first
            FetchFromCoinGeckoProxyAsync,   // Good for XAUUSD and other crypto proxies if configured
            FetchFromFrankfurterSmartAsync, // Tries to get current and yesterday's for Forex
            FetchFromExchangeRateHostAsync,
            // Add a very basic gold price API if one is found as a last resort for XAUUSD
            // FetchFromBasicGoldApiFallbackAsync,
        };

        private static readonly Dictionary<string, (decimal Price, DateTime Timestamp)> _previousPriceStaticCache = new Dictionary<string, (decimal, DateTime)>();
        private static readonly object _staticCacheLock = new object();
        private const int StaleCacheFallbackDurationHours_Static = 6;
        private const int StaticPreviousPriceCacheMaxAgeHours_Default = 30;
        private readonly MarketDataSettings _marketDataSettingsOptions; // Add this field

        public MarketDataService(
            ILogger<MarketDataService> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<CurrencyInfoSettings> currencySettingsOptions, // Keep this for currency specific info
            IOptions<MarketDataSettings> marketDataSettingsOptions) // Inject IOptions<MarketDataSettings>
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _currencySettings = currencySettingsOptions.Value ?? new CurrencyInfoSettings { Currencies = new Dictionary<string, CurrencyDetails>(StringComparer.OrdinalIgnoreCase) };
            _marketDataSettingsOptions = marketDataSettingsOptions.Value ?? new MarketDataSettings(); // Get the value
        }

        public async Task<MarketData> GetMarketDataAsync(string symbol, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            string normalizedSymbol = symbol.ToUpperInvariant();
            _logger.LogInformation("GetMarketDataAsync for {Symbol}. ForceRefresh: {ForceRefresh}", normalizedSymbol, forceRefresh);
            var currencyInfo = GetCurrencyInfo(normalizedSymbol);

            var marketData = new MarketData
            {
                Symbol = normalizedSymbol,
                CurrencyName = currencyInfo.Name,
                Description = currencyInfo.Description,
                Price = 0m,
                Change24h = 0m,
                High24h = 0m,
                Low24h = 0m,
                Volume = 0m,
                MarketCap = 0m,
                RSI = 50m,
                MACD = "N/A (History)",
                Support = 0m,
                Resistance = 0m,
                Volatility = 0m,
                Trend = "N/A",
                MarketSentiment = "N/A",
                PriceChangePercentage7d = 0m,
                PriceChangePercentage30d = 0m,
                CoinGeckoId = currencyInfo.CoinGeckoId,
                DataSource = "Unavailable",
                IsPriceLive = false,
                LastUpdated = DateTime.MinValue,
                Remarks = new List<string>(),
                Insights = new List<string>()
            };

            if (forceRefresh)
            {
                marketData.Remarks.Add("Refresh requested by user.");
                // No direct cache to clear for the static cache, but new data will overwrite.
            }

            var client = _httpClientFactory.CreateClient("MarketDataFreeApis");
            string baseAsset = currencyInfo.BaseAsset ?? (normalizedSymbol.Length >= 3 ? normalizedSymbol.Substring(0, 3) : string.Empty);
            string quoteAsset = currencyInfo.QuoteAsset ?? (normalizedSymbol.Length >= 6 ? normalizedSymbol.Substring(3, 3) : "USD");

            if (string.IsNullOrEmpty(baseAsset) || string.IsNullOrEmpty(quoteAsset))
            {
                marketData.Remarks.Add($"Invalid symbol structure: {normalizedSymbol}. Could not determine base/quote assets.");
                marketData.LastUpdated = DateTime.UtcNow; // Still update this
                return marketData;
            }

            for (int i = 0; i < _apiFetchStrategies.Count; i++)
            {
                var fetchStrategy = _apiFetchStrategies[i];
                if (cancellationToken.IsCancellationRequested) { marketData.Remarks.Add("Operation cancelled."); break; }

                _logger.LogDebug("Trying API strategy #{Number} for {Symbol}", i + 1, normalizedSymbol);
                var (price, change24h, high24, low24, volume, dataSource, rawResponse) =
                    await fetchStrategy(client, normalizedSymbol, baseAsset, quoteAsset, currencyInfo, _logger, forceRefresh, cancellationToken);

                if (price.HasValue && price.Value != 0) // CRITICAL: Ensure price is not zero
                {
                    marketData.Price = Math.Round(price.Value, currencyInfo.DisplayDecimalPlaces ?? 4);
                    marketData.IsPriceLive = true;
                    marketData.DataSource = dataSource ?? $"API Strategy {i + 1}";
                    marketData.LastUpdated = DateTime.UtcNow;
                    marketData.Remarks.Add($"Price from {marketData.DataSource}.");

                    if (change24h.HasValue) { marketData.Change24h = Math.Round(change24h.Value, 2); marketData.Remarks.Add($"24h change from API."); }
                    if (high24.HasValue) marketData.High24h = Math.Round(high24.Value, currencyInfo.DisplayDecimalPlaces ?? 4);
                    if (low24.HasValue) marketData.Low24h = Math.Round(low24.Value, currencyInfo.DisplayDecimalPlaces ?? 4);
                    if (volume.HasValue) marketData.Volume = volume.Value;

                    if (!change24h.HasValue && marketData.Price > 0) // Calculate 24h change if API didn't provide it
                    {
                        Calculate24hChangeFromStaticCache(marketData, currencyInfo, forceRefresh);
                    }

                    _logger.LogInformation("Successfully fetched live price for {Symbol} using {DataSource}.", normalizedSymbol, marketData.DataSource);
                    break; // Exit loop on successful, non-zero price fetch
                }
                else if (price.HasValue && price.Value == 0)
                {
                    _logger.LogWarning("API strategy #{Number} for {Symbol} returned a price of 0. Treating as fetch failure.", i + 1, normalizedSymbol);
                    marketData.Remarks.Add($"API {dataSource ?? $"Strategy {i + 1}"} returned zero price.");
                }
                else
                {
                    _logger.LogDebug("API strategy #{Number} for {Symbol} did not return a price.", i + 1, normalizedSymbol);
                    marketData.Remarks.Add($"API {dataSource ?? $"Strategy {i + 1}"} failed or no data.");
                }
            }

            if (!marketData.IsPriceLive || marketData.Price == 0) // Final check after all strategies
            {
                marketData.Remarks.Add($"Failed to fetch valid live price from all APIs.");
                marketData.IsPriceLive = false; // Ensure this is false
                marketData.DataSource = "Unavailable";
                marketData.LastUpdated = DateTime.UtcNow; // Time of the failed attempt

                // Try to load from static cache as a last resort if not forcing a refresh
                if (!forceRefresh)
                {
                    lock (_staticCacheLock)
                    {
                        if (_previousPriceStaticCache.TryGetValue(normalizedSymbol, out var prevCacheEntry) &&
                            (DateTime.UtcNow - prevCacheEntry.Timestamp).TotalHours < StaleCacheFallbackDurationHours_Static)
                        {
                            marketData.Price = prevCacheEntry.Price;
                            marketData.DataSource = "Static Cache (Stale)";
                            marketData.LastUpdated = prevCacheEntry.Timestamp; // Use cache entry timestamp
                            marketData.Remarks.Add($"Using stale price from static cache: {prevCacheEntry.Price:N} @ {prevCacheEntry.Timestamp:g}");
                            // 24h change cannot be reliably calculated against itself from stale cache
                            marketData.Change24h = 0; // Or mark as N/A
                            marketData.Remarks.Add("24h change N/A (stale data).");
                        }
                    }
                }
            }
            else // Live price was fetched successfully
            {
                // Ensure static cache is updated with the latest live price
                lock (_staticCacheLock)
                {
                    _previousPriceStaticCache[normalizedSymbol] = (marketData.Price, marketData.LastUpdated);
                    _logger.LogDebug("Updated static cache for {Symbol} with Price: {Price}, Timestamp: {Timestamp}", normalizedSymbol, marketData.Price, marketData.LastUpdated);
                }
            }


            CalculateDerivedFields(marketData, currencyInfo);
            return marketData;
        }

        private void Calculate24hChangeFromStaticCache(MarketData md, CurrencyDetails currencyInfo, bool forceRefresh)
        {
            if (md.Price == 0m) return;
            lock (_staticCacheLock)
            {
                if (_previousPriceStaticCache.TryGetValue(md.Symbol, out var prevCacheEntry))
                {
                    // --- CORRECTED ACCESS TO SETTING ---
                    int maxAgeHours = _marketDataSettings.StaticPreviousPriceCacheMaxAgeHours;
                    if (maxAgeHours <= 0) maxAgeHours = 30; // Fallback if setting is invalid

                    if ((DateTime.UtcNow - prevCacheEntry.Timestamp).TotalHours < maxAgeHours && prevCacheEntry.Price != 0m)
                    {
                        md.Change24h = Math.Round(((md.Price - prevCacheEntry.Price) / prevCacheEntry.Price) * 100, 2);
                        md.Remarks.Add($"24hΔ calc vs prev ({prevCacheEntry.Price.ToString($"N{currencyInfo.DisplayDecimalPlaces ?? 4}")} @ {prevCacheEntry.Timestamp:HH:mm}).");
                    }
                    else { md.Remarks.Add($"Prev. price for 24hΔ calc too old/zero (Age: {(DateTime.UtcNow - prevCacheEntry.Timestamp).TotalHours:F1}h / Max: {maxAgeHours}h)."); }
                }
                else { md.Remarks.Add("No prev. price for 24hΔ calc."); }
            }
        }



        // --- API Fetch Strategy Implementations ---
        private static async Task<(decimal? Price, decimal? Change24hPercent, decimal? High24h, decimal? Low24h, decimal? Volume, string? DataSourceName, JsonElement? RawResponse)> FetchFromFrankfurterSmartAsync(
            HttpClient client, string fullSymbol, string baseAsset, string quoteAsset, CurrencyDetails currencyInfo, ILogger logger, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (fullSymbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) || fullSymbol.Equals("XAGUSD", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(currencyInfo.CoinGeckoId))
                return (null, null, null, null, null, null, null); // Frankfurter is for Forex

            string latestUrl = $"https://api.frankfurter.app/latest?from={baseAsset}&to={quoteAsset}";
            string yesterdayDate = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
            string historicalUrl = $"https://api.frankfurter.app/{yesterdayDate}?from={baseAsset}&to={quoteAsset}";

            decimal? currentPrice = null;
            decimal? prevDayPrice = null;
            decimal? change24h = null;
            JsonElement? latestResponseJson = null;

            try // Fetch current price
            {
                logger.LogDebug("FrankfurterSmart: Attempting LATEST {Url}", latestUrl);
                var response = await client.GetAsync(latestUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var jsonDoc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
                    latestResponseJson = jsonDoc.RootElement;
                    if (latestResponseJson.Value.TryGetProperty("rates", out var rates) &&
                        rates.TryGetProperty(quoteAsset, out var rateElement) &&
                        rateElement.TryGetDecimal(out var priceVal) && priceVal != 0)
                    {
                        currentPrice = priceVal;
                        logger.LogInformation("FrankfurterSmart: Success LATEST for {Symbol}. Price: {Price}", fullSymbol, currentPrice);
                    }
                    else { logger.LogWarning("FrankfurterSmart: Could not parse LATEST rate for {Symbol} from: {Json}", fullSymbol, latestResponseJson.ToString()); }
                }
                else { logger.LogWarning("FrankfurterSmart: Failed LATEST for {Symbol}. Status: {StatusCode}", fullSymbol, response.StatusCode); }
            }
            catch (Exception ex) { logger.LogError(ex, "FrankfurterSmart: Error LATEST for {Symbol}", fullSymbol); }

            if (!currentPrice.HasValue) return (null, null, null, null, null, null, null); // If no current price, bail

            try // Fetch yesterday's price for 24h change
            {
                logger.LogDebug("FrankfurterSmart: Attempting HISTORICAL {Url}", historicalUrl);
                var response = await client.GetAsync(historicalUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var jsonDoc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
                    if (jsonDoc.RootElement.TryGetProperty("rates", out var rates) &&
                        rates.TryGetProperty(quoteAsset, out var rateElement) &&
                        rateElement.TryGetDecimal(out var priceVal) && priceVal != 0)
                    {
                        prevDayPrice = priceVal;
                        logger.LogInformation("FrankfurterSmart: Success HISTORICAL for {Symbol}. PrevPrice: {Price}", fullSymbol, prevDayPrice);
                    }
                    else { logger.LogWarning("FrankfurterSmart: Could not parse HISTORICAL rate for {Symbol} from: {Json}", fullSymbol, jsonDoc.RootElement.ToString()); }
                }
                else { logger.LogWarning("FrankfurterSmart: Failed HISTORICAL for {Symbol}. Status: {StatusCode}", fullSymbol, response.StatusCode); }
            }
            catch (Exception ex) { logger.LogError(ex, "FrankfurterSmart: Error HISTORICAL for {Symbol}", fullSymbol); }

            if (currentPrice.HasValue && prevDayPrice.HasValue && prevDayPrice.Value != 0)
            {
                change24h = ((currentPrice.Value - prevDayPrice.Value) / prevDayPrice.Value) * 100;
            }

            return (currentPrice, change24h, null, null, null, "Frankfurter.app (Smart)", latestResponseJson);
        }


        private static async Task<(decimal? Price, decimal? Change24hPercent, decimal? High24h, decimal? Low24h, decimal? Volume, string? DataSourceName, JsonElement? RawResponse)> FetchFromExchangeRateHostAsync(
             HttpClient client, string fullSymbol, string baseAsset, string quoteAsset, CurrencyDetails currencyInfo, ILogger logger, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(currencyInfo.CoinGeckoId)) return (null, null, null, null, null, null, null);
            string url = $"https://api.exchangerate.host/latest?base={baseAsset}&symbols={quoteAsset}&source=ecb";
            try
            {
                logger.LogDebug("ExchangeRateHost: Attempting {Url}", url);
                var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) { logger.LogWarning("ExchangeRateHost: Failed {Symbol}. Status: {Code}", fullSymbol, response.StatusCode); return (null, null, null, null, null, null, null); }
                using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var jsonDoc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
                if (jsonDoc.RootElement.TryGetProperty("rates", out var rates) && rates.TryGetProperty(quoteAsset, out var rateEl) && rateEl.TryGetDecimal(out var price) && price != 0)
                {
                    logger.LogInformation("ExchangeRateHost: Success {Symbol}. Price: {Price}", fullSymbol, price);
                    return (price, null, null, null, null, "ExchangeRate.host", jsonDoc.RootElement);
                }
                logger.LogWarning("ExchangeRateHost: Could not parse rate for {Symbol} from: {Json}", fullSymbol, jsonDoc.RootElement.ToString());
            }
            catch (Exception ex) { logger.LogError(ex, "ExchangeRateHost: Error for {Symbol}", fullSymbol); }
            return (null, null, null, null, null, null, null);
        }

        private static async Task<(decimal? Price, decimal? Change24hPercent, decimal? High24h, decimal? Low24h, decimal? Volume, string? DataSourceName, JsonElement? RawResponse)> FetchFromCoinGeckoProxyAsync(
            HttpClient client, string fullSymbol, string baseAsset, string quoteAsset, CurrencyDetails currencyInfo, ILogger logger, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(currencyInfo.CoinGeckoId) || string.IsNullOrEmpty(currencyInfo.CoinGeckoPriceCurrency))
                return (null, null, null, null, null, null, null);
            string url = $"https://api.coingecko.com/api/v3/coins/{currencyInfo.CoinGeckoId}?localization=false&tickers=false&market_data=true&community_data=false&developer_data=false&sparkline=false";
            try
            {
                logger.LogDebug("CoinGeckoProxy: Attempting {Symbol} via {ID} ({Url})", fullSymbol, currencyInfo.CoinGeckoId, url);
                var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) { logger.LogWarning("CoinGeckoProxy: Failed for {ID}. Status: {Code}", currencyInfo.CoinGeckoId, response.StatusCode); return (null, null, null, null, null, null, null); }
                using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var jsonDoc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("market_data", out var mdNode))
                {
                    decimal? price = null; decimal? change24h = null; decimal? high24 = null; decimal? low24 = null; decimal? volume = null;
                    string priceCurr = currencyInfo.CoinGeckoPriceCurrency.ToLowerInvariant();

                    if (mdNode.TryGetProperty("current_price", out var cpN) && cpN.TryGetProperty(priceCurr, out var pN) && pN.TryGetDecimal(out var pVal) && pVal != 0) price = pVal;
                    else { logger.LogWarning("CoinGeckoProxy: No current_price.{Pc} for {ID}", priceCurr, currencyInfo.CoinGeckoId); return (null, null, null, null, null, null, root); }

                    if (mdNode.TryGetProperty("price_change_percentage_24h_in_currency", out var pcp24N) && pcp24N.TryGetProperty(priceCurr, out var pcp24ValN) && pcp24ValN.TryGetDecimal(out var pcp24Val)) change24h = pcp24Val;
                    else if (mdNode.TryGetProperty("price_change_percentage_24h", out var basePcp24N) && basePcp24N.TryGetDecimal(out var basePcp24)) change24h = basePcp24;

                    if (mdNode.TryGetProperty("high_24h", out var h24N) && h24N.TryGetProperty(priceCurr, out var h24ValN) && h24ValN.TryGetDecimal(out var hVal)) high24 = hVal;
                    if (mdNode.TryGetProperty("low_24h", out var l24N) && l24N.TryGetProperty(priceCurr, out var l24ValN) && l24ValN.TryGetDecimal(out var lVal)) low24 = lVal;
                    if (mdNode.TryGetProperty("total_volume", out var tvN) && tvN.TryGetProperty(priceCurr, out var tvValN) && tvValN.TryGetDecimal(out var vVal)) volume = vVal;

                    logger.LogInformation("CoinGeckoProxy: Success {Symbol} via {ID}. Price: {Price}", fullSymbol, currencyInfo.CoinGeckoId, price);
                    return (price, change24h, high24, low24, volume, $"CoinGecko ({currencyInfo.CoinGeckoId})", root);
                }
                logger.LogWarning("CoinGeckoProxy: 'market_data' node not found for {ID}. Response: {Json}", currencyInfo.CoinGeckoId, root.ToString());
            }
            catch (Exception ex) { logger.LogError(ex, "CoinGeckoProxy: Error for {ID}", currencyInfo.CoinGeckoId); }
            return (null, null, null, null, null, null, null);
        }

        private void CalculateDerivedFields(MarketData md, CurrencyDetails currencyInfo)
        {
            md.Remarks = md.Remarks ?? new List<string>();
            md.Insights = md.Insights ?? new List<string>(); // Ensure Insights is initialized

            if (!md.IsPriceLive || md.Price == 0)
            {
                ResetCalculatedFieldsToUnavailable(md);
                md.Remarks.Add("Calculations skipped (no live price / price is zero).");
                md.Insights.Add("Live market data is currently unavailable for this symbol.");
                return;
            }

            // Trend
            if (md.Change24h > 0.75m) { md.Trend = "Strong Uptrend 🚀"; md.Insights.Add("Indicates a strong upward momentum in the last 24 hours."); }
            else if (md.Change24h < -0.75m) { md.Trend = "Strong Downtrend 📉"; md.Insights.Add("Indicates a strong downward momentum in the last 24 hours."); }
            else if (md.Change24h > 0.15m) { md.Trend = "Uptrend ↗️"; md.Insights.Add("Showing a slight upward trend recently."); }
            else if (md.Change24h < -0.15m) { md.Trend = "Downtrend ↘️"; md.Insights.Add("Showing a slight downward trend recently."); }
            else if (md.Change24h != 0 || HasRecentPriceMovement(md.Symbol, md.Price)) { md.Trend = "Sideways ➡️"; md.Insights.Add("Price movement appears to be consolidating or range-bound."); }
            else md.Trend = "N/A (No Change Data)";
            if (md.Trend != "N/A (No Change Data)") md.Remarks.Add($"Trend derived from 24h change ({md.Change24h:F2}%).");
            else md.Remarks.Add("Trend indeterminable due to lack of 24h price change data.");


            // Market Sentiment
            if (md.Trend.Contains("Uptrend")) { md.MarketSentiment = "Bullish 🐂"; md.Insights.Add("Overall market sentiment appears bullish based on recent price action."); }
            else if (md.Trend.Contains("Downtrend")) { md.MarketSentiment = "Bearish 🐻"; md.Insights.Add("Overall market sentiment appears bearish based on recent price action."); }
            else if (md.Trend == "Sideways ➡️") { md.MarketSentiment = "Neutral ⚪️"; md.Insights.Add("Market sentiment is currently neutral, awaiting clearer direction."); }
            else md.MarketSentiment = "N/A";

            // Pseudo RSI
            if (md.Change24h != 0 || md.Trend != "N/A (No Change Data)" || HasRecentPriceMovement(md.Symbol, md.Price))
            {
                md.RSI = Math.Clamp(50 + (md.Change24h * 10), 5, 95);
                md.Remarks.Add($"RSI ({md.RSI:F0}) is a basic estimation from 24h change.");
                if (md.RSI > 70) md.Insights.Add($"Estimated RSI ({md.RSI:F0}) suggests potentially overbought conditions.");
                else if (md.RSI < 30) md.Insights.Add($"Estimated RSI ({md.RSI:F0}) suggests potentially oversold conditions.");
                else md.Insights.Add($"Estimated RSI ({md.RSI:F0}) indicates neutral momentum.");
            }
            else
            {
                md.RSI = 50; md.Remarks.Add("RSI neutral (no 24h change).");
                md.Insights.Add("RSI is neutral due to lack of significant price change data.");
            }

            // Volatility
            if (md.High24h > 0 && md.Low24h > 0 && md.Low24h < md.High24h && md.Price > 0)
            {
                md.Volatility = Math.Round(((md.High24h - md.Low24h) / md.Price) * 100, 2);
                md.Remarks.Add($"Daily Volatility (H-L/P): {md.Volatility:F2}%.");
                md.Insights.Add(md.Volatility > 2.0m ? "Relatively high daily volatility observed." : (md.Volatility < 0.5m ? "Low daily volatility observed." : "Moderate daily volatility."));
            }
            else if (md.Price > 0 && (md.Change24h != 0 || HasRecentPriceMovement(md.Symbol, md.Price)))
            {
                md.Volatility = Math.Round(Math.Abs(md.Change24h) * 0.6m + 0.05m, 2);
                md.Volatility = Math.Clamp(md.Volatility, 0.02m, 15.0m);
                md.Remarks.Add($"Daily Volatility (est.): {md.Volatility:F2}%.");
                md.Insights.Add($"Estimated daily volatility is around {md.Volatility:F2}%.");
            }
            else { md.Remarks.Add("Daily Volatility: N/A."); md.Insights.Add("Daily volatility could not be determined."); }

            // Support & Resistance
            int displayDecimals = currencyInfo.DisplayDecimalPlaces ?? 4;
            bool srFromHL = false;
            if (md.Low24h > 0 && md.Price > md.Low24h) { md.Support = Math.Round(md.Low24h, displayDecimals); md.Remarks.Add("S: 24h Low."); srFromHL = true; }
            else if (md.Price > 0) { md.Support = Math.Round(md.Price * (1 - (Math.Max(0.3m, md.Volatility) / 100m) * 0.7m), displayDecimals); md.Remarks.Add("S (est.)."); }

            if (md.High24h > 0 && md.Price < md.High24h) { md.Resistance = Math.Round(md.High24h, displayDecimals); md.Remarks.Add("R: 24h High."); srFromHL = true; }
            else if (md.Price > 0) { md.Resistance = Math.Round(md.Price * (1 + (Math.Max(0.3m, md.Volatility) / 100m) * 0.7m), displayDecimals); md.Remarks.Add("R (est.)."); }

            if (md.Support > 0 && md.Resistance > 0 && md.Support >= md.Resistance)
            { // Sanity check
                md.Support = Math.Round(md.Price * 0.995m, displayDecimals); md.Resistance = Math.Round(md.Price * 1.005m, displayDecimals);
                md.Remarks[md.Remarks.Count - 1] = "S/R (est. adjusted).";
            }
            if (md.Support > 0 && md.Resistance > 0) md.Insights.Add($"Key levels: Support approx. {md.Support.ToString($"N{displayDecimals}")}, Resistance approx. {md.Resistance.ToString($"N{displayDecimals}")}.");
            else md.Insights.Add("Support/Resistance levels could not be reliably estimated.");


            md.MACD = "N/A (More Data Needed)";
            md.Remarks.Add("MACD requires historical series data, unavailable from current sources.");
        }

        private bool HasRecentPriceMovement(string symbol, decimal currentPrice)
        {
            lock (_staticCacheLock) { if (_previousPriceStaticCache.TryGetValue(symbol, out var prev) && prev.Price != currentPrice) return true; }
            return false;
        }
        private void ResetCalculatedFieldsToUnavailable(MarketData md)
        {
            md.RSI = 50m; md.MACD = "N/A"; md.Volatility = 0m; md.Support = 0m; md.Resistance = 0m;
            md.Trend = "N/A"; md.MarketSentiment = "N/A"; md.Change24h = 0m;
        }
        private CurrencyDetails GetCurrencyInfo(string symbol)
        {
            var currencies = _currencySettings?.Currencies ?? new Dictionary<string, CurrencyDetails>(StringComparer.OrdinalIgnoreCase);
            if (currencies.TryGetValue(symbol, out var info))
            {
                if (!string.IsNullOrEmpty(info.CoinGeckoId) && string.IsNullOrEmpty(info.CoinGeckoPriceCurrency)) info.CoinGeckoPriceCurrency = "usd";
                if (!info.DisplayDecimalPlaces.HasValue) info.DisplayDecimalPlaces = (info.BaseAsset == "JPY" || info.QuoteAsset == "JPY" || symbol.Contains("JPY")) ? 2 : (symbol == "XAUUSD" || symbol == "XAGUSD") ? 2 : 4;
                return info;
            }
            _logger.LogWarning("CurrencyInfo for {Symbol} not in settings. Using dynamic default.", symbol);
            string baseC = symbol.Length >= 3 ? symbol.Substring(0, 3) : symbol;
            string quoteC = symbol.Length >= 6 ? symbol.Substring(3, 3) : "USD";
            return new CurrencyDetails { Name = $"{baseC}/{quoteC}", Description = $"Data for {baseC}/{quoteC}.", Category = "Forex", IsActive = true, BaseAsset = baseC, QuoteAsset = quoteC, CoinGeckoId = null, CoinGeckoPriceCurrency = "usd", DisplayDecimalPlaces = (baseC == "JPY" || quoteC == "JPY") ? 2 : 4 };
        }
    
    // Helper class for deserializing frankfurter.app response
    internal class FrankfurterResponse
        {
            public decimal Amount { get; set; }
            public string Base { get; set; }
            public DateTime Date { get; set; }
            public Dictionary<string, decimal> Rates { get; set; }
        }

        public class MarketDataException : Exception
        {
            public MarketDataException(string message) : base(message) { }
            public MarketDataException(string message, Exception innerException)
                : base(message, innerException) { }
        }
    }
}