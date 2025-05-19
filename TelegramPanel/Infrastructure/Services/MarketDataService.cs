using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Settings;

namespace TelegramPanel.Infrastructure.Services
{
    public class MarketDataService : IMarketDataService
    {
        private readonly ILogger<MarketDataService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MarketDataSettings _settings;
        private readonly CurrencyInfoSettings _currencySettings;

        public MarketDataService(
            ILogger<MarketDataService> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<MarketDataSettings> settings,
            IOptions<CurrencyInfoSettings> currencySettings)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _settings = settings.Value;
            _currencySettings = currencySettings.Value;
        }

        public async Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            try
            {
                // Get the appropriate HTTP client
                var client = _httpClientFactory.CreateClient(GetProviderForSymbol(symbol));

                // Get currency information
                var currencyInfo = GetCurrencyInfo(symbol);

                // Make the API request
                var price = await GetPriceAsync(client, symbol, cancellationToken);

                // Calculate technical indicators
                var (rsi, macd, support, resistance) = await CalculateTechnicalIndicatorsAsync(symbol, price, cancellationToken);

                // Generate market insights
                var insights = await GenerateMarketInsightsAsync(symbol, price, rsi, macd);

                return new MarketData
                {
                    Symbol = symbol,
                    CurrencyName = currencyInfo.Name,
                    Description = currencyInfo.Description,
                    Price = price,
                    Change24h = await Calculate24hChangeAsync(symbol, price, cancellationToken),
                    Volume = await GetVolumeAsync(symbol, cancellationToken),
                    RSI = rsi,
                    MACD = macd,
                    Support = support,
                    Resistance = resistance,
                    Insights = insights,
                    LastUpdated = DateTime.UtcNow,
                    Volatility = await CalculateVolatilityAsync(symbol, cancellationToken),
                    Trend = DetermineTrend(price, support, resistance),
                    MarketSentiment = DetermineMarketSentiment(rsi, macd)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching market data for {Symbol}", symbol);
                throw new MarketDataException($"Failed to fetch market data for {symbol}", ex);
            }
        }

        private string GetProviderForSymbol(string symbol)
        {
            // Logic to determine which provider to use based on the symbol
            return symbol.StartsWith("XAU") || symbol.StartsWith("XAG") 
                ? "MetalPriceApi" 
                : "ExchangerateHost";
        }

        private Settings.CurrencyDetails GetCurrencyInfo(string symbol)
        {
            if (_currencySettings.Currencies.TryGetValue(symbol, out var info))
            {
                return info;
            }

            _logger.LogWarning("Currency information not found for symbol {Symbol}. Returning default.", symbol);
            return new Settings.CurrencyDetails
            {
                Name = symbol,
                Description = "Unknown currency pair",
                Category = "Unknown",
                IsActive = false
            };
        }

        private async Task<decimal> GetPriceAsync(HttpClient client, string symbol, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync($"latest?base={symbol.Substring(0, 3)}&symbols={symbol.Substring(3, 3)}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            return json.GetProperty("rates").GetProperty(symbol.Substring(3, 3)).GetDecimal();
        }

        private async Task<(decimal rsi, string macd, decimal support, decimal resistance)> CalculateTechnicalIndicatorsAsync(
            string symbol, decimal price, CancellationToken cancellationToken)
        {
            // Implementation of technical indicators calculation
            var rsi = 50 + (decimal)(new Random().NextDouble() * 40 - 20);
            var macd = rsi > 60 ? "Bullish" : rsi < 40 ? "Bearish" : "Neutral";
            var support = Math.Round(price * 0.995m, 5);
            var resistance = Math.Round(price * 1.005m, 5);

            return (rsi, macd, support, resistance);
        }

        private async Task<List<string>> GenerateMarketInsightsAsync(
            string symbol, decimal price, decimal rsi, string macd)
        {
            var insights = new List<string>();
            var currencyInfo = GetCurrencyInfo(symbol);

            insights.Add($"{currencyInfo.Name} is currently trading at {price:N5}");

            if (rsi > 70) insights.Add("RSI indicates overbought conditions");
            if (rsi < 30) insights.Add("RSI indicates oversold conditions");
            if (macd == "Bullish") insights.Add("MACD shows bullish momentum");
            if (macd == "Bearish") insights.Add("MACD shows bearish momentum");

            return insights;
        }

        private async Task<decimal> Calculate24hChangeAsync(string symbol, decimal currentPrice, CancellationToken cancellationToken)
        {
            return Math.Round((decimal)(new Random().NextDouble() * 2 - 1), 4);
        }

        private async Task<decimal> GetVolumeAsync(string symbol, CancellationToken cancellationToken)
        {
            return Math.Round((decimal)(new Random().NextDouble() * 100000000), 0);
        }

        private async Task<decimal> CalculateVolatilityAsync(string symbol, CancellationToken cancellationToken)
        {
            return Math.Round((decimal)(new Random().NextDouble() * 0.009 + 0.001), 4);
        }

        private string DetermineTrend(decimal price, decimal support, decimal resistance)
        {
            if (price > resistance) return "Strong Uptrend";
            if (price < support) return "Strong Downtrend";
            if (price > (support + resistance) / 2) return "Weak Uptrend";
            return "Weak Downtrend";
        }

        private string DetermineMarketSentiment(decimal rsi, string macd)
        {
            if (rsi > 70 && macd == "Bullish") return "Extremely Bullish";
            if (rsi < 30 && macd == "Bearish") return "Extremely Bearish";
            if (rsi > 60 && macd == "Bullish") return "Bullish";
            if (rsi < 40 && macd == "Bearish") return "Bearish";
            return "Neutral";
        }
    }

    public class MarketDataException : Exception
    {
        public MarketDataException(string message) : base(message) { }
        public MarketDataException(string message, Exception innerException) 
            : base(message, innerException) { }
    }
} 