using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    public class MarketDataService : IMarketDataService
    {
        private readonly ILogger<MarketDataService> _logger;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, string> _apiEndpoints;
        private readonly Dictionary<string, (string Name, string Description)> _currencyInfo;

        public MarketDataService(
            ILogger<MarketDataService> logger,
            HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;

            // Initialize API endpoints for different currencies
            _apiEndpoints = new Dictionary<string, string>
            {
                { "XAUUSD", "https://api.metalpriceapi.com/v1/latest" }, // Example API for gold
                { "XAGUSD", "https://api.metalpriceapi.com/v1/latest" }, // Example API for silver
                { "DEFAULT", "https://api.exchangerate.host/convert" }    // Default forex API
            };

            // Initialize currency information
            _currencyInfo = new Dictionary<string, (string Name, string Description)>
            {
                { "XAUUSD", ("Gold", "Precious metal, safe haven asset") },
                { "XAGUSD", ("Silver", "Precious metal, industrial use") },
                { "EURUSD", ("Euro/US Dollar", "Major forex pair, most traded") },
                { "GBPUSD", ("British Pound/US Dollar", "Major forex pair, cable") },
                { "USDJPY", ("US Dollar/Japanese Yen", "Major forex pair, safe haven") },
                { "AUDUSD", ("Australian Dollar/US Dollar", "Commodity currency") },
                { "USDCAD", ("US Dollar/Canadian Dollar", "Oil-linked currency") },
                { "USDCHF", ("US Dollar/Swiss Franc", "Safe haven currency") },
                { "NZDUSD", ("New Zealand Dollar/US Dollar", "Commodity currency") }
            };
        }

        public async Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            try
            {
                // Validate and normalize symbol
                symbol = ValidateAndNormalizeSymbol(symbol);
                
                // Get currency information
                var (currencyName, currencyDescription) = GetCurrencyInfo(symbol);

                // Get the appropriate API endpoint
                var endpoint = GetApiEndpoint(symbol);
                
                // Make the API request with retry logic
                var price = await GetPriceWithRetryAsync(symbol, endpoint, cancellationToken);

                // Calculate technical indicators
                var (rsi, macd, support, resistance) = await CalculateTechnicalIndicatorsAsync(symbol, price, cancellationToken);

                // Generate market insights
                var insights = await GenerateMarketInsightsAsync(symbol, price, rsi, macd);

                // Create market data object
                var marketData = new MarketData
                {
                    Symbol = symbol,
                    CurrencyName = currencyName,
                    Description = currencyDescription,
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

                return marketData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching market data for {Symbol}", symbol);
                throw new MarketDataException($"Failed to fetch market data for {symbol}", ex);
            }
        }

        private string ValidateAndNormalizeSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol cannot be empty");

            symbol = symbol.ToUpper();
            
            if (symbol != "XAUUSD" && symbol != "XAGUSD" && symbol.Length != 6)
                throw new ArgumentException("Invalid symbol format");

            return symbol;
        }

        private (string Name, string Description) GetCurrencyInfo(string symbol)
        {
            return _currencyInfo.TryGetValue(symbol, out var info) 
                ? info 
                : (symbol, "Forex currency pair");
        }

        private string GetApiEndpoint(string symbol)
        {
            return _apiEndpoints.TryGetValue(symbol, out var endpoint) 
                ? endpoint 
                : _apiEndpoints["DEFAULT"];
        }

        private async Task<decimal> GetPriceWithRetryAsync(string symbol, string endpoint, CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            var retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var (from, to) = GetCurrencyPair(symbol);
                    var url = $"{endpoint}?from={from}&to={to}";
                    
                    _logger.LogDebug("Fetching price from {Url}", url);
                    var response = await _httpClient.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                    
                    // Log the response for debugging
                    _logger.LogDebug("API Response: {Response}", json.ToString());

                    // Handle exchangerate.host response format
                    if (endpoint.Contains("exchangerate.host"))
                    {
                        if (json.TryGetProperty("result", out var resultElement))
                        {
                            return resultElement.GetDecimal();
                        }
                        if (json.TryGetProperty("rates", out var ratesElement))
                        {
                            if (ratesElement.TryGetProperty(to, out var rateElement))
                            {
                                return rateElement.GetDecimal();
                            }
                        }
                    }
                    // Handle metalpriceapi.com response format
                    else if (endpoint.Contains("metalpriceapi.com"))
                    {
                        if (json.TryGetProperty("rates", out var ratesElement))
                        {
                            if (ratesElement.TryGetProperty("USD", out var usdElement))
                            {
                                return usdElement.GetDecimal();
                            }
                        }
                    }

                    // If we get here, we couldn't parse the response
                    _logger.LogWarning("Could not parse price from API response for {Symbol}. Response: {Response}", 
                        symbol, json.ToString());
                    throw new MarketDataException($"Could not parse price from API response for {symbol}");
                }
                catch (Exception ex) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    _logger.LogWarning(ex, "Retry {RetryCount} for {Symbol}", retryCount, symbol);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), cancellationToken);
                }
            }

            throw new MarketDataException($"Failed to fetch price for {symbol} after {maxRetries} retries");
        }

        private (string from, string to) GetCurrencyPair(string symbol)
        {
            if (symbol == "XAUUSD") return ("XAU", "USD");
            if (symbol == "XAGUSD") return ("XAG", "USD");
            return (symbol.Substring(0, 3), symbol.Substring(3, 3));
        }

        private async Task<(decimal rsi, string macd, decimal support, decimal resistance)> CalculateTechnicalIndicatorsAsync(
            string symbol, decimal price, CancellationToken cancellationToken)
        {
            // In a real implementation, you would:
            // 1. Fetch historical data
            // 2. Calculate RSI using standard formula
            // 3. Calculate MACD using standard formula
            // 4. Calculate support/resistance using technical analysis

            // For demo purposes, we'll generate some reasonable values
            var rsi = 50 + (decimal)(new Random().NextDouble() * 40 - 20); // 30-70 range
            var macd = rsi > 60 ? "Bullish" : rsi < 40 ? "Bearish" : "Neutral";
            var support = price * 0.995m;
            var resistance = price * 1.005m;

            return (rsi, macd, support, resistance);
        }

        private async Task<List<string>> GenerateMarketInsightsAsync(
            string symbol, decimal price, decimal rsi, string macd)
        {
            var insights = new List<string>();
            var (currencyName, _) = GetCurrencyInfo(symbol);

            // Add price-based insights
            insights.Add($"{currencyName} is currently trading at {price:N5}");

            // Add technical analysis insights
            if (rsi > 70) insights.Add("RSI indicates overbought conditions");
            if (rsi < 30) insights.Add("RSI indicates oversold conditions");
            if (macd == "Bullish") insights.Add("MACD shows bullish momentum");
            if (macd == "Bearish") insights.Add("MACD shows bearish momentum");

            // Add time-based insights
            var hour = DateTime.UtcNow.Hour;
            if (hour >= 14 && hour < 16) insights.Add("Fed rate decision impact");
            if (hour >= 8 && hour < 10) insights.Add("European session volatility");

            return insights;
        }

        private async Task<decimal> Calculate24hChangeAsync(string symbol, decimal currentPrice, CancellationToken cancellationToken)
        {
            // In a real implementation, you would fetch historical data
            // For demo, return a random change between -1% and +1%
            return Math.Round((decimal)(new Random().NextDouble() * 2 - 1), 4);
        }

        private async Task<decimal> GetVolumeAsync(string symbol, CancellationToken cancellationToken)
        {
            // In a real implementation, you would fetch actual volume data
            // For demo, return a random volume
            return Math.Round((decimal)(new Random().NextDouble() * 100000000), 0);
        }

        private async Task<decimal> CalculateVolatilityAsync(string symbol, CancellationToken cancellationToken)
        {
            // In a real implementation, you would calculate actual volatility
            // For demo, return a random volatility between 0.1% and 1%
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