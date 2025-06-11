// -----------------
// CORRECTED FILE
// -----------------
using Application.Common.Interfaces.CoinGeckoApiClient;
using Application.DTOs.CoinGecko;
using Microsoft.Extensions.Logging;
using Shared.Results;
using System.Net.Http.Json;
using System.Text.Json;

namespace Application.Features.Crypto.Services.CoinGecko
{
    /// <summary>
    /// Concrete implementation of ICoinGeckoApiClient using HttpClient.
    /// This service handles direct communication with the public CoinGecko API (v3)
    /// and does not require an API key for its endpoints.
    /// </summary>
    public class CoinGeckoApiClient : ICoinGeckoApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CoinGeckoApiClient> _logger;
        private const string BaseUrl = "https://api.coingecko.com/api/v3";

        public CoinGeckoApiClient(HttpClient httpClient, ILogger<CoinGeckoApiClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            // --- FIX START ---
            // Add a default User-Agent header to the HttpClient instance.
            // Many public APIs (including CoinGecko) will return a 403 Forbidden error
            // if this header is missing, as a measure to block anonymous bots.
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForexTradingBot/1.0");
            // --- FIX END ---
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
                var response = await _httpClient.GetFromJsonAsync<JsonElement>(requestUrl, cancellationToken);

                if (response.TryGetProperty("coins", out var coinsArray))
                {
                    var trendingResults = coinsArray.Deserialize<List<TrendingCoinResult>>();
                    var trendingCoins = trendingResults?
                        .Select(r => r.Item)
                        .Where(item => item != null)
                        .ToList() ?? [];

                    return Result<List<TrendingCoinDto>>.Success(trendingCoins!);
                }

                _logger.LogWarning("CoinGecko API response for trending coins did not contain a 'coins' array.");
                return Result<List<TrendingCoinDto>>.Failure("Invalid API response format.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An exception occurred while fetching trending coins from CoinGecko API.");
                return Result<List<TrendingCoinDto>>.Failure($"An error occurred: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches detailed information for a single coin by its ID.
        /// </summary>
        public async Task<Result<CoinDetailsDto>> GetCoinDetailsAsync(string id, CancellationToken cancellationToken)
        {
            var requestUrl = $"{BaseUrl}/coins/{id}?localization=false&tickers=false&market_data=true&community_data=false&developer_data=false&sparkline=false";
            _logger.LogInformation("Requesting coin details for ID {Id} from CoinGecko API.", id);

            try
            {
                var details = await _httpClient.GetFromJsonAsync<CoinDetailsDto>(requestUrl, cancellationToken);
                if (details == null)
                {
                    _logger.LogWarning("CoinGecko API returned a null response for coin ID {Id}.", id);
                    return Result<CoinDetailsDto>.Failure($"API returned no data for coin '{id}'.");
                }
                return Result<CoinDetailsDto>.Success(details);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An exception occurred while fetching details for coin ID {Id}.", id);
                return Result<CoinDetailsDto>.Failure($"An error occurred while fetching details for '{id}': {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches a paginated list of coins from CoinGecko's /coins/markets endpoint.
        /// </summary>
        public async Task<Result<List<CoinMarketDto>>> GetCoinMarketsAsync(int page, int perPage, CancellationToken cancellationToken)
        {
            var requestUrl = $"{BaseUrl}/coins/markets?vs_currency=usd&order=market_cap_desc&per_page={perPage}&page={page}&sparkline=false";
            _logger.LogInformation("Requesting coin markets from CoinGecko API. Page: {Page}, PerPage: {PerPage}", page, perPage);

            try
            {
                var markets = await _httpClient.GetFromJsonAsync<List<CoinMarketDto>>(requestUrl, cancellationToken);
                if (markets == null)
                {
                    _logger.LogWarning("CoinGecko API returned a null response for coin markets.");
                    return Result<List<CoinMarketDto>>.Failure("API returned no data.");
                }
                return Result<List<CoinMarketDto>>.Success(markets);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An exception occurred while fetching coin markets from CoinGecko API.");
                return Result<List<CoinMarketDto>>.Failure($"An error occurred: {ex.Message}");
            }
        }
    }
}