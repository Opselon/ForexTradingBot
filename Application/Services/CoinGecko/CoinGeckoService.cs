// -----------------
// UPDATED FILE
// -----------------
using Application.Common.Interfaces;
using Application.Common.Interfaces.CoinGeckoApiClient;
using Application.DTOs.CoinGecko;
using Application.Features.Crypto.Interfaces;
using Microsoft.Extensions.Logging;
using Shared.Results;

namespace Application.Services.CoinGecko
{
    public class CoinGeckoService : ICoinGeckoService
    {
        private readonly ICoinGeckoApiClient _apiClient;
        private readonly ILogger<CoinGeckoService> _logger;
        // Inject cache services for both DTO types
        private readonly IMemoryCacheService<List<CoinMarketDto>> _marketsCache;
        private readonly IMemoryCacheService<CoinDetailsDto> _detailsCache;

        public CoinGeckoService(
            ICoinGeckoApiClient apiClient,
            ILogger<CoinGeckoService> logger,
            IMemoryCacheService<List<CoinMarketDto>> marketsCache,
            IMemoryCacheService<CoinDetailsDto> detailsCache)
        {
            _apiClient = apiClient;
            _logger = logger;
            _marketsCache = marketsCache;
            _detailsCache = detailsCache;
        }

        public async Task<Result<List<CoinMarketDto>>> GetCoinMarketsAsync(int page, int perPage, CancellationToken cancellationToken)
        {
            string cacheKey = $"CoinMarkets_Page_{page}";

            // 1. Try to get data from cache first
            if (_marketsCache.TryGetValue(cacheKey, out var cachedMarkets))
            {
                _logger.LogInformation("Cache HIT for coin markets on page {Page}.", page);
                return Result<List<CoinMarketDto>>.Success(cachedMarkets!);
            }

            _logger.LogInformation("Cache MISS for coin markets on page {Page}. Fetching from API.", page);

            // 2. If not in cache, fetch from API
            var result = await _apiClient.GetCoinMarketsAsync(page, perPage, cancellationToken);

            // 3. If API call was successful, store the result in cache
            if (result.Succeeded && result.Data != null)
            {
                _marketsCache.Set(cacheKey, result.Data, TimeSpan.FromSeconds(90)); // Cache for 90 seconds
            }

            return result;
        }

        public async Task<Result<CoinDetailsDto>> GetCryptoDetailsAsync(string id, CancellationToken cancellationToken)
        {
            string cacheKey = $"CoinDetails_{id}";

            // 1. Try to get data from cache
            if (_detailsCache.TryGetValue(cacheKey, out var cachedDetails))
            {
                _logger.LogInformation("Cache HIT for coin details: {Id}", id);
                return Result<CoinDetailsDto>.Success(cachedDetails!);
            }

            _logger.LogInformation("Cache MISS for coin details: {Id}. Fetching from API.", id);

            // 2. If not in cache, fetch
            var result = await _apiClient.GetCoinDetailsAsync(id, cancellationToken);

            // 3. If successful, cache it
            if (result.Succeeded && result.Data != null)
            {
                _detailsCache.Set(cacheKey, result.Data, TimeSpan.FromMinutes(5)); // Details can be cached for longer
            }

            return result;
        }

        // The GetTrendingCoinsAsync can also be cached if needed, but it's less critical.
        public async Task<Result<List<TrendingCoinDto>>> GetTrendingCoinsAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Application service fetching trending cryptocurrencies from CoinGecko.");
            return await _apiClient.GetTrendingCoinsAsync(cancellationToken);
        }
    }
}