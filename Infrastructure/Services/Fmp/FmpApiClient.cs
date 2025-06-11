// -----------------
// NEW FILE
// -----------------
using Application.Common.Interfaces;
using Application.DTOs.Fmp;
using Microsoft.Extensions.Logging;
using Shared.Results;
using System.Net.Http.Json;

namespace Infrastructure.Services.Fmp
{
    /// <summary>
    /// Concrete implementation of IFmpApiClient using HttpClient.
    /// This service handles direct communication with the financialmodelingprep.com API.
    /// </summary>
    public class FmpApiClient : IFmpApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<FmpApiClient> _logger;

        // As per user request, the API key is hardcoded directly here.
        private const string ApiKey = "bXpRTlBPTToPl3TgztFZneqSanKwMnMF";
        private const string BaseUrl = "https://financialmodelingprep.com/api/v3";

        public FmpApiClient(HttpClient httpClient, ILogger<FmpApiClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForexTradingBot/1.0");
        }

        /// <summary>
        /// Fetches all crypto quotes from the FMP bulk endpoint.
        /// </summary>
        public async Task<Result<List<FmpQuoteDto>>> GetCryptoQuotesAsync(CancellationToken cancellationToken)
        {
            var requestUrl = $"{BaseUrl}/quotes/crypto?apikey={ApiKey}";
            _logger.LogInformation("Requesting all crypto quotes from FMP API.");

            try
            {
                var quotes = await _httpClient.GetFromJsonAsync<List<FmpQuoteDto>>(requestUrl, cancellationToken);
                if (quotes == null)
                {
                    _logger.LogWarning("FMP API returned a null response for all crypto quotes.");
                    return Result<List<FmpQuoteDto>>.Failure("FMP API returned no data.");
                }
                return Result<List<FmpQuoteDto>>.Success(quotes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An exception occurred while fetching all crypto quotes from FMP API.");
                return Result<List<FmpQuoteDto>>.Failure($"FMP API error: {ex.Message}");
            }
        }
    }
}