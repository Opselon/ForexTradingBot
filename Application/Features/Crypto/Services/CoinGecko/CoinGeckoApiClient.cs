using Application.Common.Interfaces.CoinGeckoApiClient;
using Application.DTOs.CoinGecko;
using Microsoft.Extensions.Logging;
using Polly; // Required for Polly
using Polly.Extensions.Http; // Required for HttpPolicyExtensions
using Polly.Retry; // Required for AsyncRetryPolicy
using Shared.Results; // Assuming your Result<T> is here
using System.Net; // For HttpStatusCode
using System.Net.Http.Json; // For GetFromJsonAsync
using System.Text.Json; // For JsonElement, JsonSerializer

namespace Application.Features.Crypto.Services.CoinGecko
{
    /// <summary>
    /// Concrete implementation of ICoinGeckoApiClient using HttpClient.
    /// This service handles direct communication with the public CoinGecko API (v3)
    /// and does not require an API key for its endpoints. Includes retry logic for transient errors.
    /// </summary>
    public class CoinGeckoApiClient : ICoinGeckoApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CoinGeckoApiClient> _logger;
        private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy; // FIX: Add Polly retry policy
        private const string BaseUrl = "https://api.coingecko.com/api/v3";

        public CoinGeckoApiClient(HttpClient httpClient, ILogger<CoinGeckoApiClient> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Add a default User-Agent header
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("YourAppName/1.0 (Contact: your@email.com)"); // FIX: Use a more descriptive User-Agent

            // --- FIX: Configure Polly Retry Policy for HTTP Calls ---
            _retryPolicy = HttpPolicyExtensions
                // Retry on standard HTTP transient error codes (5xx, 408 Request Timeout)
                // and specifically on 429 (Too Many Requests) which is common for rate limits
                .HandleTransientHttpError()
                .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests) // Handle 429 explicitly
                                                                                   // Add more specific status codes if needed, e.g., HttpStatusCode.Conflict (409) if it indicates a race condition
                                                                                   // Wait and retry based on attempt number, with logging
                .WaitAndRetryAsync(
                    retryCount: 3, // Number of retry attempts
                    sleepDurationProvider: retryAttempt =>
                    {
                        // Implement exponential backoff with jitter
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)); // 2, 4, 8 seconds
                        var jitter = TimeSpan.FromMilliseconds(new Random().Next(0, 500)); // Add random jitter
                        var finalDelay = delay + jitter;
                        _logger.LogWarning("CoinGecko API call failed (attempt {Attempt}). Retrying in {Delay:F2} seconds...", retryAttempt, finalDelay.TotalSeconds);
                        return finalDelay;
                    },
                    onRetryAsync: (outcome, timespan, retryAttempt, context) =>
                    {
                        // Optional: Log more details on *what* failed before the delay
                        if (outcome.Exception != null)
                        {
                            _logger.LogError(outcome.Exception, "Retry {Attempt} for CoinGecko API call failed with exception.", retryAttempt);
                        }
                        else if (outcome.Result != null)
                        {
                            _logger.LogWarning("Retry {Attempt} for CoinGecko API call failed with status code {StatusCode}.", retryAttempt, outcome.Result.StatusCode);
                        }
                        return Task.CompletedTask; // onRetryAsync needs to return Task
                    }
                );
    
        }

        /// <summary>
        /// Fetches the top-7 trending coins from CoinGecko's public trending endpoint.
        /// </summary>
        public async Task<Result<List<TrendingCoinDto>>> GetTrendingCoinsAsync(CancellationToken cancellationToken)
        {
            var requestUrl = $"{BaseUrl}/search/trending";
            _logger.LogInformation("Requesting trending coins from CoinGecko API.");

            try
            {
                // --- FIX: Use the retry policy to execute the HTTP call ---
                var response = await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    // Use GetAsync to get HttpResponseMessage first, then read JSON
                    // This allows the retry policy to inspect the status code directly
                    var res = await _httpClient.GetAsync(requestUrl, ct);
                    res.EnsureSuccessStatusCode(); // Throws for non-success status codes (handled by HandleTransientHttpError)
                    return res;
                }, cancellationToken);
                // --- END FIX ---

                // Read the response body after successful execution (potentially after retries)
                var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);


                if (jsonResponse.TryGetProperty("coins", out var coinsArray))
                {
                    // Assuming Deserialize<List<TrendingCoinResult>>() maps correctly
                    var trendingResults = jsonResponse.Deserialize<List<TrendingCoinResult>>();
                    var trendingCoins = trendingResults?
                        .Select(r => r.Item)
                        .Where(item => item != null)
                        .ToList() ?? [];

                    if (!trendingCoins.Any())
                    {
                        _logger.LogWarning("CoinGecko API response for trending coins contained an empty or invalid 'coins' array after deserialization.");
                        return Result<List<TrendingCoinDto>>.Failure("API returned no trending data.");
                    }

                    _logger.LogInformation("Successfully fetched {Count} trending coins from CoinGecko API.", trendingCoins.Count);
                    return Result<List<TrendingCoinDto>>.Success(trendingCoins!);
                }

                _logger.LogWarning("CoinGecko API response for trending coins did not contain a 'coins' array or it was in an unexpected format.");
                return Result<List<TrendingCoinDto>>.Failure("Invalid API response format.");
            }
            catch (Exception ex) // Catch the final exception after all retries
            {
                _logger.LogError(ex, "Final failure after retries: An exception occurred while fetching trending coins from CoinGecko API.");
                return Result<List<TrendingCoinDto>>.Failure($"Failed to fetch trending coins after multiple attempts: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches detailed information for a single coin by its ID.
        /// </summary>
        // Parameter name should ideally be coinGeckoId here, as this client requires the provider's ID.
        // The mapping from symbol to this ID happens in the orchestrator.
        public async Task<Result<CoinDetailsDto>> GetCoinDetailsAsync(string coinGeckoId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(coinGeckoId))
            {
                _logger.LogWarning("Attempted to fetch CoinGecko details with null or whitespace ID.");
                return Result<CoinDetailsDto>.Failure("CoinGecko ID cannot be null or empty.");
            }

            var requestUrl = $"{BaseUrl}/coins/{coinGeckoId}?localization=false&tickers=false&market_data=true&community_data=false&developer_data=false&sparkline=false";
            _logger.LogInformation("Requesting coin details for ID '{CoinGeckoId}' from CoinGecko API.", coinGeckoId);

            try
            {
                // --- FIX: Use the retry policy to execute the HTTP call ---
                var response = await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    var res = await _httpClient.GetAsync(requestUrl, ct);
                    res.EnsureSuccessStatusCode(); // Throws for non-success status codes (handled by HandleTransientHttpError)
                    return res;
                }, cancellationToken);
                // --- END FIX ---

                // Read the response body after successful execution (potentially after retries)
                var details = await response.Content.ReadFromJsonAsync<CoinDetailsDto>(cancellationToken: cancellationToken);

                if (details == null)
                {
                    _logger.LogWarning("CoinGecko API returned a null response for coin ID '{CoinGeckoId}'.", coinGeckoId);
                    return Result<CoinDetailsDto>.Failure($"API returned no data for coin '{coinGeckoId}'.");
                }

                // Basic check if crucial data is missing, even if call succeeded
                if (string.IsNullOrWhiteSpace(details.Id))
                {
                    _logger.LogWarning("CoinGecko API returned partial/invalid data for coin ID '{CoinGeckoId}'. Missing core ID.", coinGeckoId);
                    return Result<CoinDetailsDto>.Failure($"API returned incomplete data for coin '{coinGeckoId}'.");
                }


                _logger.LogInformation("Successfully fetched details for coin ID '{CoinGeckoId}' from CoinGecko API.", coinGeckoId);
                return Result<CoinDetailsDto>.Success(details);
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.NotFound) // Catch specific 404 after retries
            {
                _logger.LogWarning(httpEx, "CoinGecko API returned 404 Not Found for coin ID '{CoinGeckoId}'.", coinGeckoId);
                // Return a Failure result indicating not found, handled by Orchestrator fallback
                return Result<CoinDetailsDto>.Failure($"Coin '{coinGeckoId}' not found on CoinGecko.");
            }
            catch (Exception ex) // Catch the final exception after all retries (including other HTTP errors)
            {
                _logger.LogError(ex, "Final failure after retries: An exception occurred while fetching details for coin ID '{CoinGeckoId}' from CoinGecko API.", coinGeckoId);
                return Result<CoinDetailsDto>.Failure($"Failed to fetch details for '{coinGeckoId}' after multiple attempts: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches a paginated list of coins from CoinGecko's /coins/markets endpoint.
        /// </summary>
        public async Task<Result<List<CoinMarketDto>>> GetCoinMarketsAsync(int page, int perPage, CancellationToken cancellationToken)
        {
            if (page < 1 || perPage < 1)
            {
                _logger.LogWarning("Attempted to fetch coin markets with invalid page ({Page}) or perPage ({PerPage}).", page, perPage);
                return Result<List<CoinMarketDto>>.Failure("Invalid pagination parameters.");
            }

            var requestUrl = $"{BaseUrl}/coins/markets?vs_currency=usd&order=market_cap_desc&per_page={perPage}&page={page}&sparkline=false";
            _logger.LogInformation("Requesting coin markets from CoinGecko API. Page: {Page}, PerPage: {PerPage}", page, perPage);

            try
            {
                // --- FIX: Use the retry policy to execute the HTTP call ---
                var response = await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    var res = await _httpClient.GetAsync(requestUrl, ct);
                    res.EnsureSuccessStatusCode(); // Throws for non-success status codes (handled by HandleTransientHttpError)
                    return res;
                }, cancellationToken);
                // --- END FIX ---

                // Read the response body after successful execution (potentially after retries)
                var markets = await response.Content.ReadFromJsonAsync<List<CoinMarketDto>>(cancellationToken: cancellationToken);

                if (markets == null)
                {
                    _logger.LogWarning("CoinGecko API returned a null response for coin markets.");
                    return Result<List<CoinMarketDto>>.Failure("API returned no data.");
                }

                _logger.LogInformation("Successfully fetched {Count} coin markets from CoinGecko API (Page {Page}, PerPage {PerPage}).", markets.Count, page, perPage);
                return Result<List<CoinMarketDto>>.Success(markets);
            }
            catch (Exception ex) // Catch the final exception after all retries
            {
                _logger.LogError(ex, "Final failure after retries: An exception occurred while fetching coin markets from CoinGecko API (Page {Page}, PerPage {PerPage}).", page, perPage);
                return Result<List<CoinMarketDto>>.Failure($"Failed to fetch coin markets after multiple attempts: {ex.Message}");
            }
        }
    }
}