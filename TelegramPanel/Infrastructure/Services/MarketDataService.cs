using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Settings;
using System.Globalization;
using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace TelegramPanel.Infrastructure.Services
{
    public class MarketDataService : IMarketDataService
    {
        private readonly ILogger<MarketDataService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CurrencyInfoSettings _currencySettings;

        private static readonly Random _random = new Random();

        // Define constants for fxhistoricaldata.com
        private const string FxHistoricalDataApiBaseUrl = "http://api.fxhistoricaldata.com/";
        private const string FrankfurterApiBaseUrl = "https://api.frankfurter.app/"; // Added for Frankfurter
        private const string BinanceApiBaseUrl = "https://api.binance.com/api/v3/";
        private const string GoldUsdProxySymbolBinance = "PAXGUSDT";

        private const string CoinGeckoApiBaseUrl = "https://api.coingecko.com/api/v3/";

        // Using Tether Gold as a proxy for XAU/USD via CoinGecko
        private const string XauUsdCoinGeckoId = "tether-gold";

        public MarketDataService(
            ILogger<MarketDataService> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<CurrencyInfoSettings> currencySettings)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _currencySettings = currencySettings.Value;
        }

        public async Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to fetch or generate market data for {Symbol}", symbol);
            var currencyInfo = GetCurrencyInfo(symbol);

            var marketData = new MarketData
            {
                Symbol = symbol,
                CurrencyName = currencyInfo.Name,
                Description = currencyInfo.Description,
                // Initialize with random/default values, to be overwritten by API data
                Price = GetRandomizedPrice(symbol),
                Change24h = Math.Round((decimal)(_random.NextDouble() * 2 - 1), 2),
                Volume = (long)(_random.NextDouble() * 10000000),
                RSI = (decimal)(_random.NextDouble() * 70 + 15),
                MACD = "N/A (Random)",
                Volatility = Math.Round((decimal)(_random.NextDouble() * 1 + 0.1), 2),
                High24h = 0,
                Low24h = 0,
                MarketCap = 0,
                PriceChangePercentage7d = 0,
                PriceChangePercentage30d = 0,
                Insights = new List<string>(),
                CoinGeckoId = symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? XauUsdCoinGeckoId : null,
                LastUpdated = DateTime.UtcNow
            };

            bool fetchedLivePrice = false;
            var client = _httpClientFactory.CreateClient();

            if (symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase))
            {
                string fullCoinGeckoUrl =
                    $"{CoinGeckoApiBaseUrl}coins/{XauUsdCoinGeckoId}?localization=false&tickers=false&market_data=true&community_data=false&developer_data=false&sparkline=false";
                try
                {
                    _logger.LogInformation("Fetching XAUUSD (proxy via CoinGecko ID: {CoinGeckoId}) data from: {Url}",
                        XauUsdCoinGeckoId, fullCoinGeckoUrl);
                    var response = await client.GetAsync(fullCoinGeckoUrl, cancellationToken);
                    var responseContent =
                        await response.Content
                            .ReadAsStringAsync(cancellationToken); // Read content for logging in case of error too

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("CoinGecko raw response for {ID}: {Content}", XauUsdCoinGeckoId,
                            responseContent);
                        var cgData = JsonSerializer.Deserialize<CoinGeckoCoinData>(responseContent,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (cgData?.Market_Data != null)
                        {
                            decimal? usdPriceOfPaxG =
                                cgData.Market_Data.Current_Price?.TryGetValue("usd", out var cpUsdVal) == true
                                    ? cpUsdVal
                                    : null;
                            decimal? xauPriceOfPaxG =
                                cgData.Market_Data.Current_Price?.TryGetValue("xau", out var cpXauVal) == true
                                    ? cpXauVal
                                    : null;
                            bool specificPriceSet = false;

                            // Primary path: Convert PAXG/USD to XAU/USD using PAXG/XAU rate
                            if (usdPriceOfPaxG.HasValue && xauPriceOfPaxG.HasValue && xauPriceOfPaxG.Value != 0)
                            {
                                marketData.Price = usdPriceOfPaxG.Value / xauPriceOfPaxG.Value;

                                if (cgData.Market_Data.High_24h?.TryGetValue("usd", out var h24Usd) == true &&
                                    h24Usd.HasValue)
                                    marketData.High24h = h24Usd.Value / xauPriceOfPaxG.Value;
                                else
                                    marketData.High24h = marketData.Price; // Fallback to current adjusted price

                                if (cgData.Market_Data.Low_24h?.TryGetValue("usd", out var l24Usd) == true &&
                                    l24Usd.HasValue)
                                    marketData.Low24h = l24Usd.Value / xauPriceOfPaxG.Value;
                                else
                                    marketData.Low24h = marketData.Price; // Fallback to current adjusted price

                                if (cgData.Market_Data.Market_Cap?.TryGetValue("usd", out var mcUsd) == true &&
                                    mcUsd.HasValue)
                                    marketData.MarketCap = mcUsd.Value / xauPriceOfPaxG.Value;

                                fetchedLivePrice = true;
                                specificPriceSet = true;
                                _logger.LogInformation(
                                    "Successfully calculated XAU/USD price ({Price}) and related data from CoinGecko PAXG data (via XAU conversion).",
                                    marketData.Price);
                            }
                            // Fallback path: Use PAXG/USD directly if conversion not possible
                            else if (usdPriceOfPaxG.HasValue)
                            {
                                marketData.Price = usdPriceOfPaxG.Value;

                                if (cgData.Market_Data.High_24h?.TryGetValue("usd", out var h24Usd) == true &&
                                    h24Usd.HasValue)
                                    marketData.High24h = h24Usd.Value;
                                else
                                    marketData.High24h = marketData.Price;

                                if (cgData.Market_Data.Low_24h?.TryGetValue("usd", out var l24Usd) == true &&
                                    l24Usd.HasValue)
                                    marketData.Low24h = l24Usd.Value;
                                else
                                    marketData.Low24h = marketData.Price;

                                if (cgData.Market_Data.Market_Cap?.TryGetValue("usd", out var mcUsd) == true &&
                                    mcUsd.HasValue)
                                    marketData.MarketCap = mcUsd.Value;

                                fetchedLivePrice = true;
                                specificPriceSet = true;
                                _logger.LogWarning(
                                    "Used direct PAXG/USD price for XAUUSD as XAU conversion factor was unavailable. Price: {Price}. High/Low/MC will also be PAXG/USD based.",
                                    marketData.Price);
                            }

                            if (specificPriceSet)
                            {
                                // Percentage changes: Prefer *_in_currency["xau"], fallback to ["usd"]
                                marketData.Change24h =
                                    cgData.Market_Data.Price_Change_Percentage_24h_In_Currency?.TryGetValue("xau",
                                        out var pcp24hXau) == true && pcp24hXau.HasValue
                                        ? pcp24hXau.Value
                                        : (cgData.Market_Data.Price_Change_Percentage_24h_In_Currency?.TryGetValue(
                                            "usd", out var pcp24hUsd) == true && pcp24hUsd.HasValue
                                            ? pcp24hUsd.Value
                                            : marketData.Change24h);

                                marketData.PriceChangePercentage7d =
                                    cgData.Market_Data.Price_Change_Percentage_7d_In_Currency?.TryGetValue("xau",
                                        out var pcp7dXau) == true && pcp7dXau.HasValue
                                        ? pcp7dXau.Value
                                        : (cgData.Market_Data.Price_Change_Percentage_7d_In_Currency?.TryGetValue("usd",
                                            out var pcp7dUsd) == true && pcp7dUsd.HasValue
                                            ? pcp7dUsd.Value
                                            : marketData.PriceChangePercentage7d);

                                marketData.PriceChangePercentage30d =
                                    cgData.Market_Data.Price_Change_Percentage_30d_In_Currency?.TryGetValue("xau",
                                        out var pcp30dXau) == true && pcp30dXau.HasValue
                                        ? pcp30dXau.Value
                                        : (cgData.Market_Data.Price_Change_Percentage_30d_In_Currency?.TryGetValue(
                                            "usd", out var pcp30dUsd) == true && pcp30dUsd.HasValue
                                            ? pcp30dUsd.Value
                                            : marketData.PriceChangePercentage30d);

                                // Volume remains PAXG Total Volume in USD
                                marketData.Volume =
                                    cgData.Market_Data.Total_Volume?.TryGetValue("usd", out var tv) == true &&
                                    tv.HasValue
                                        ? tv.Value
                                        : marketData.Volume;
                                _logger.LogInformation(
                                    "Successfully parsed additional CoinGecko data (Changes, Volume) for {ID}",
                                    XauUsdCoinGeckoId);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Could not determine price for {ID} from CoinGecko market_data. Current_Price USD and/or XAU fields might be missing or null.",
                                    XauUsdCoinGeckoId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Could not parse Market_Data from CoinGecko response for {ID}. Content: {Content}",
                                XauUsdCoinGeckoId, responseContent);
                        }
                    }
                    else
                    {
                        _logger.LogError(
                            "Error fetching data from CoinGecko for {ID}. Status: {StatusCode}, Reason: {Reason}, URL: {Url}, Content: {Content}",
                            XauUsdCoinGeckoId, response.StatusCode, response.ReasonPhrase, fullCoinGeckoUrl,
                            responseContent);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception fetching/parsing CoinGecko data for {ID}. URL: {Url}",
                        XauUsdCoinGeckoId, fullCoinGeckoUrl);
                }
            }
            else if (symbol.Length == 6) // Standard Forex pairs
            {
                string baseCurrency = symbol.Substring(0, 3);
                string quoteCurrency = symbol.Substring(3, 3);
                string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                string yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
                string frankfurterLatestUrl = $"{FrankfurterApiBaseUrl}latest?from={baseCurrency}&to={quoteCurrency}";
                string frankfurterYesterdayUrl =
                    $"{FrankfurterApiBaseUrl}{yesterday}?from={baseCurrency}&to={quoteCurrency}";

                try
                {
                    _logger.LogInformation("Fetching {Symbol} current price from Frankfurter: {Url}", symbol,
                        frankfurterLatestUrl);
                    var todayResponse = await client.GetAsync(frankfurterLatestUrl, cancellationToken);
                    if (todayResponse.IsSuccessStatusCode)
                    {
                        var frankfurterData = await JsonSerializer.DeserializeAsync<FrankfurterResponse>(
                            await todayResponse.Content.ReadAsStreamAsync(cancellationToken),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
                        if (frankfurterData?.Rates != null &&
                            frankfurterData.Rates.TryGetValue(quoteCurrency, out var rate))
                        {
                            marketData.Price = rate;
                            fetchedLivePrice = true;
                            _logger.LogInformation("Successfully fetched {Symbol} price from Frankfurter: {Price}",
                                symbol, marketData.Price);

                            _logger.LogInformation("Fetching {Symbol} yesterday's price from Frankfurter: {Url}",
                                symbol, frankfurterYesterdayUrl);
                            var yesterdayResponse = await client.GetAsync(frankfurterYesterdayUrl, cancellationToken);
                            if (yesterdayResponse.IsSuccessStatusCode)
                            {
                                var yesterdayData = await JsonSerializer.DeserializeAsync<FrankfurterResponse>(
                                    await yesterdayResponse.Content.ReadAsStreamAsync(cancellationToken),
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                                    cancellationToken);
                                if (yesterdayData?.Rates != null &&
                                    yesterdayData.Rates.TryGetValue(quoteCurrency, out var prevRate) && prevRate != 0)
                                {
                                    marketData.Change24h = Math.Round(((marketData.Price - prevRate) / prevRate) * 100,
                                        2);
                                    _logger.LogInformation(
                                        "Successfully calculated 24h change for {Symbol}: {Change24h}%", symbol,
                                        marketData.Change24h);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "Could not parse {Symbol} yesterday's rate or it was zero from Frankfurter.",
                                        symbol);
                                }
                            }
                            else
                            {
                                _logger.LogError(
                                    "Error fetching {Symbol} yesterday's price from Frankfurter. Status: {StatusCode}, Reason: {Reason}",
                                    symbol, yesterdayResponse.StatusCode, yesterdayResponse.ReasonPhrase);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Could not parse {Symbol} rate from Frankfurter response.", symbol);
                        }
                    }
                    else
                    {
                        _logger.LogError(
                            "Error fetching {Symbol} current price from Frankfurter. Status: {StatusCode}, Reason: {Reason}",
                            symbol, todayResponse.StatusCode, todayResponse.ReasonPhrase);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching data for {Symbol} from Frankfurter.", symbol);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Symbol {Symbol} is not XAUUSD and not a standard 6-char Forex pair. Using random data only.",
                    symbol);
            }

            // If price is still the initial random value, log that API fetch failed
            if (!fetchedLivePrice)
            {
                _logger.LogWarning(
                    "CRITICAL FALLBACK: Using randomized price for {Symbol} as live data could not be fetched from APIs.",
                    symbol);
                // Keep other random values initialized at the start
            }

            marketData.Trend = DetermineTrend(marketData.Change24h);
            marketData.MarketSentiment = DetermineMarketSentiment(marketData.Trend);
            if (marketData.Support == 0 && marketData.Price != 0)
                marketData.Support = marketData.Price * (1 - (decimal)(_random.NextDouble() * 0.02 + 0.005));
            if (marketData.Resistance == 0 && marketData.Price != 0)
                marketData.Resistance = marketData.Price * (1 + (decimal)(_random.NextDouble() * 0.02 + 0.005));

            // Populate Insights
            marketData.Insights.Clear();
            if (symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase))
            {
                marketData.Insights.Add(fetchedLivePrice
                    ? "Gold (XAU/USD) data from CoinGecko."
                    : "Gold (XAU/USD) data from CoinGecko failed, showing simulated data.");
                marketData.Insights.Add(
                    "Technical indicators (RSI, MACD, ATR) for Gold are currently N/A or simulated.");
            }
            else // Forex
            {
                marketData.Insights.Add(fetchedLivePrice
                    ? $"{symbol} data from frankfurter.app."
                    : $"{symbol} data from frankfurter.app failed, showing simulated data.");
                marketData.Insights.Add($"Technical indicators for {symbol} are N/A or simulated.");
            }

            if (!fetchedLivePrice)
            {
                marketData.Insights.Add("Note: Displaying simulated data due to API fetch issues.");
            }

            marketData.Insights.Add($"Trend (from 24h change): {marketData.Trend}");
            marketData.Insights.Add($"Sentiment (from trend): {marketData.MarketSentiment}");


            return marketData;
        }

        private Settings.CurrencyDetails GetCurrencyInfo(string symbol)
        {
            string lookupSymbol = symbol;
            // We might not have "tether-gold" in settings, so we provide a specific override for XAUUSD display name
            // Or, ensure CurrencyInfoSettings has an entry for "tether-gold" if you want to configure its display name there.

            if (_currencySettings.Currencies.TryGetValue(lookupSymbol, out var info))
            {
                if (symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase))
                {
                    // Override name for display if original request was XAUUSD
                    info.Name = "Gold (XAU/USD)";
                    info.Description = "Spot Gold price via CoinGecko.";
                }

                return info;
            }

            _logger.LogWarning(
                "Currency information not found for symbol {Symbol} (lookup: {LookupSymbol}) in CurrencyInfoSettings. Returning default.",
                symbol, lookupSymbol);
            string baseCurrency = symbol.Length >= 3 ? symbol.Substring(0, 3) : symbol;
            string quoteCurrency = symbol.Length >= 6 ? symbol.Substring(3, 3) : "USD";

            return new Settings.CurrencyDetails
            {
                Name = symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase)
                    ? "Gold (XAU/USD)"
                    : $"{baseCurrency}/{quoteCurrency}",
                Description = symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase)
                    ? "Spot Gold price via CoinGecko."
                    : $"Standard {baseCurrency} to {quoteCurrency} exchange rate.",
                Category = symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? "Commodity (Proxy)" : "Forex",
                IsActive = true
            };
        }

        private decimal GetRandomizedPrice(string symbol)
        {
            if (symbol.Contains("JPY")) return (decimal)(_random.NextDouble() * 100 + 100);
            if (symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase))
                return (decimal)(_random.NextDouble() * 500 + 1800);
            return (decimal)(_random.NextDouble() * 0.5 + 0.8);
        }

        private string DetermineTrend(decimal change24h)
        {
            if (change24h > 0.5m) return "Strong Uptrend";
            if (change24h < -0.5m) return "Strong Downtrend";
            if (change24h > 0.1m) return "Weak Uptrend";
            if (change24h < -0.1m) return "Weak Downtrend";
            return "Sideways";
        }

        private string DetermineMarketSentiment(string trend)
        {
            if (trend.Contains("Uptrend")) return "Bullish";
            if (trend.Contains("Downtrend")) return "Bearish";
            return "Neutral";
        }

        // Helper classes for CoinGecko API
        public class CoinGeckoCoinData
        {
            public string Id { get; set; }
            public string Symbol { get; set; }
            public string Name { get; set; }
            [JsonPropertyName("market_data")] public MarketDataDetails Market_Data { get; set; }
        }

        public class MarketDataDetails
        {
            [JsonPropertyName("current_price")] public Dictionary<string, decimal?> Current_Price { get; set; }

            [JsonPropertyName("market_cap")] public Dictionary<string, decimal?> Market_Cap { get; set; }

            [JsonPropertyName("total_volume")] public Dictionary<string, decimal?> Total_Volume { get; set; }

            [JsonPropertyName("high_24h")] public Dictionary<string, decimal?> High_24h { get; set; }

            [JsonPropertyName("low_24h")] public Dictionary<string, decimal?> Low_24h { get; set; }

            [JsonPropertyName("price_change_percentage_24h")]
            public decimal? Price_Change_Percentage_24h { get; set; } // Base, usually vs USD if coin is USD priced

            [JsonPropertyName("price_change_percentage_7d")]
            public decimal? Price_Change_Percentage_7d { get; set; }

            [JsonPropertyName("price_change_percentage_30d")]
            public decimal? Price_Change_Percentage_30d { get; set; }

            // For price changes against specific currencies
            [JsonPropertyName("price_change_percentage_24h_in_currency")]
            public Dictionary<string, decimal?> Price_Change_Percentage_24h_In_Currency { get; set; }

            [JsonPropertyName("price_change_percentage_7d_in_currency")]
            public Dictionary<string, decimal?> Price_Change_Percentage_7d_In_Currency { get; set; }

            [JsonPropertyName("price_change_percentage_30d_in_currency")]
            public Dictionary<string, decimal?> Price_Change_Percentage_30d_In_Currency { get; set; }
        }

        // Helper classes for Frankfurter API (existing)
        internal class FrankfurterResponse
        {
            public decimal Amount { get; set; }
            public string Base { get; set; }
            public DateTime Date { get; set; }
            public Dictionary<string, decimal> Rates { get; set; }
        }

        public class MarketDataException : Exception
        {
            public MarketDataException(string message) : base(message)
            {
            }

            public MarketDataException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }
    }
}