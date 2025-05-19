using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Settings;

namespace TelegramPanel.Infrastructure.Services
{
    public class MarketDataService : IMarketDataService
    {
        private readonly ILogger<MarketDataService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CurrencyInfoSettings _currencySettings;
        private static readonly Random _random = new Random();

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
            decimal price;
            bool fetchedLivePrice = false;

            // Try to fetch live Forex data from Frankfurter.app
            if (!symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) && symbol.Length == 6) // Assuming standard 6-char Forex symbols like EURUSD
            {
                try
                {
                    string baseCurrency = symbol.Substring(0, 3);
                    string quoteCurrency = symbol.Substring(3, 3);
                    var client = _httpClientFactory.CreateClient("FrankfurterApiClient");
                    var response = await client.GetAsync($"latest?from={baseCurrency}&to={quoteCurrency}", cancellationToken);
                    response.EnsureSuccessStatusCode();
                    
                    using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var frankfurterResponse = await JsonSerializer.DeserializeAsync<FrankfurterResponse>(jsonStream, cancellationToken: cancellationToken);

                    if (frankfurterResponse?.Rates != null && frankfurterResponse.Rates.TryGetValue(quoteCurrency, out var rate))
                    {
                        price = rate;
                        fetchedLivePrice = true;
                        _logger.LogInformation("Successfully fetched live Forex price for {Symbol} from frankfurter.app: {Price}", symbol, price);
                    }
                    else
                    {
                        _logger.LogWarning("Could not parse rate for {QuoteCurrency} from frankfurter.app response for {Symbol}. Falling back to random.", quoteCurrency, symbol);
                        price = GetRandomizedPrice(symbol); // Fallback to random if parsing fails
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching live Forex price for {Symbol} from frankfurter.app. Falling back to random.", symbol);
                    price = GetRandomizedPrice(symbol); // Fallback to random on error
                }
            }
            else
            {
                // For XAUUSD or other non-standard symbols, use randomized price
                _logger.LogInformation("Using randomized price for {Symbol}.", symbol);
                price = GetRandomizedPrice(symbol);
            }

            decimal change24h = Math.Round((decimal)(_random.NextDouble() * 2 - 1), 2); // -1.00% to +1.00%
            string trend = DetermineTrend(change24h);
            string marketSentiment = DetermineMarketSentiment(trend);

            var marketData = new MarketData
            {
                Symbol = symbol,
                CurrencyName = currencyInfo.Name,
                Description = currencyInfo.Description,
                Price = Math.Round(price, 5),
                Change24h = change24h,
                Volume = 0, // N/A
                RSI = 0,    // Placeholder for your calculation
                MACD = "N/A", // Placeholder for your calculation
                Support = 0, // Placeholder for your calculation
                Resistance = 0, // Placeholder for your calculation
                Insights = new List<string>
                {
                    fetchedLivePrice
                        ? $"Live price for {currencyInfo.Name} is {price:N5}."
                        : $"Simulated price for {currencyInfo.Name} is {price:N5}.",
                    $"""
                     The short-term trend (simulated) appears to be {trend.ToLower()}.
                     Market sentiment is generally {marketSentiment.ToLower()}.
                     """,
                    "Note: This analysis is based on simplified data for demonstration."
                },
                LastUpdated = DateTime.UtcNow,
                Volatility = Math.Round((decimal)(_random.NextDouble() * 1 + 0.1), 2), // Random Volatility %
                Trend = trend,
                MarketSentiment = marketSentiment
            };

            return marketData;
        }

        private Settings.CurrencyDetails GetCurrencyInfo(string symbol)
        {
            if (_currencySettings.Currencies.TryGetValue(symbol, out var info))
            {
                return info;
            }

            _logger.LogWarning("Currency information not found for symbol {Symbol} in CurrencyInfoSettings. Returning default.", symbol);
            string baseCurrency = symbol.Length >= 3 ? symbol.Substring(0, 3) : symbol;
            string quoteCurrency = symbol.Length >= 6 ? symbol.Substring(3, 3) : "USD";
            
            return new Settings.CurrencyDetails
            {
                Name = $"{baseCurrency}/{quoteCurrency}",
                Description = $"Standard {baseCurrency} to {quoteCurrency} exchange rate.",
                Category = "Forex",
                IsActive = true
            };
        }

        private decimal GetRandomizedPrice(string symbol)
        {
            if (symbol.Contains("JPY"))
            {
                return (decimal)(_random.NextDouble() * 100 + 100); // e.g., 100-200 for JPY pairs
            }
            else if (symbol.Contains("XAU")) // Gold
            {
                return (decimal)(_random.NextDouble() * 500 + 1800); // e.g., 1800-2300 for XAUUSD
            }
            else
            {
                return (decimal)(_random.NextDouble() * 0.5 + 0.8); // e.g., 0.8-1.3 for most other Forex pairs
            }
        }

        private string DetermineTrend(decimal change24h)
        {
            string trend;
            if (change24h > 0.1m) trend = "Weak Uptrend";
            else if (change24h < -0.1m) trend = "Weak Downtrend";
            else trend = "Sideways";
            if (change24h > 0.5m) trend = "Strong Uptrend";
            if (change24h < -0.5m) trend = "Strong Downtrend";
            return trend;
        }

        private string DetermineMarketSentiment(string trend)
        {
            string marketSentiment;
            if (trend.Contains("Uptrend")) marketSentiment = "Bullish";
            else if (trend.Contains("Downtrend")) marketSentiment = "Bearish";
            else marketSentiment = "Neutral";
            return marketSentiment;
        }
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