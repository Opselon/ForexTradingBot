// -----------------
// NEW FILE
// -----------------
using Application.Common.Interfaces;
using Application.DTOs.Fmp;
using Application.Features.Fmp.Interfaces;
using Microsoft.Extensions.Logging;
using Shared.Results;

namespace Application.Features.Fmp.Services
{
    /// <summary>
    /// Implements the IFmpService, providing the business logic for fetching
    /// and processing cryptocurrency data from the FMP API client. This service will
    /// primarily be used as a fallback.
    /// </summary>
    public class FmpService : IFmpService
    {
        private readonly IFmpApiClient _apiClient;
        private readonly ILogger<FmpService> _logger;

        public FmpService(IFmpApiClient apiClient, ILogger<FmpService> logger)
        {
            _apiClient = apiClient;
            _logger = logger;
        }

        /// <summary>
        /// Gets a list of the top N cryptos from FMP by fetching all quotes,
        /// filtering for valid data, sorting by market cap, and taking the specified count.
        /// </summary>
        public async Task<Result<List<FmpQuoteDto>>> GetTopCryptosAsync(int count, CancellationToken cancellationToken)
        {
            _logger.LogInformation("FMP Service: Attempting to fetch top cryptocurrencies.");

            var quotesResult = await _apiClient.GetCryptoQuotesAsync(cancellationToken);

            if (!quotesResult.Succeeded || quotesResult.Data == null)
            {
                _logger.LogWarning("Failed to fetch crypto quotes from the FMP client. Errors: {Errors}", string.Join(", ", quotesResult.Errors));
                return Result<List<FmpQuoteDto>>.Failure("Could not retrieve cryptocurrency data from FMP.");
            }

            var topCryptos = quotesResult.Data
                .Where(q => q.MarketCap.HasValue && q.MarketCap > 0)
                .OrderByDescending(q => q.MarketCap)
                .Take(count)
                .ToList();

            _logger.LogInformation("FMP Service: Successfully fetched and sorted {Count} top cryptocurrencies.", topCryptos.Count);
            return Result<List<FmpQuoteDto>>.Success(topCryptos);
        }
    }
}