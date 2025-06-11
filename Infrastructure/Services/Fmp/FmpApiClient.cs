using Application.Common.Interfaces;
using Application.DTOs.Fmp;
using Microsoft.Extensions.Logging;
using Shared.Results;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
namespace Infrastructure.Services.Fmp
{
    public class FmpApiClient : IFmpApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<FmpApiClient> _logger;
        private const string ApiKey = "bXpRTlBPTToPl3TgztFZneqSanKwMnMF";
        private const string BaseUrl = "https://financialmodelingprep.com/stable";
        public FmpApiClient(HttpClient httpClient, ILogger<FmpApiClient> logger) { _httpClient = httpClient; _logger = logger; _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForexTradingBot/1.0"); }
        public async Task<Result<FmpQuoteDto>> GetFullCryptoQuoteAsync(string fmpSymbol, CancellationToken cancellationToken)
        {
            var requestUrl = $"{BaseUrl}/quote/{fmpSymbol}?apikey={ApiKey}";
            _logger.LogInformation("Requesting full quote from FMP API for {Symbol}.", fmpSymbol);
            try
            {
                var response = await _httpClient.GetAsync(requestUrl, cancellationToken); response.EnsureSuccessStatusCode();
                var quotes = await response.Content.ReadFromJsonAsync<List<FmpQuoteDto>>(cancellationToken: cancellationToken);
                if (quotes == null || !quotes.Any()) { var rawJson = await response.Content.ReadAsStringAsync(cancellationToken); _logger.LogWarning("FMP API returned a successful (200 OK) response but the data array was null or empty for symbol {Symbol}. Raw JSON: {RawJson}", fmpSymbol, rawJson); return Result<FmpQuoteDto>.Failure($"FMP API returned no quote data for {fmpSymbol}."); }
                return Result<FmpQuoteDto>.Success(quotes.First());
            }
            catch (Exception ex) { _logger.LogError(ex, "An exception occurred while fetching a full quote for {Symbol} from FMP API.", fmpSymbol); return Result<FmpQuoteDto>.Failure($"FMP API error: {ex.Message}"); }
        }
        public async Task<Result<List<FmpQuoteDto>>> GetFullCryptoQuoteListAsync(CancellationToken cancellationToken)
        {
            var requestUrl = $"{BaseUrl}/crypto?apikey={ApiKey}";
            _logger.LogInformation("Requesting all crypto quotes from FMP stable API (paid endpoint).");
            try
            {
                var quotes = await _httpClient.GetFromJsonAsync<List<FmpQuoteDto>>(requestUrl, cancellationToken);
                if (quotes == null || !quotes.Any()) { return Result<List<FmpQuoteDto>>.Failure("FMP API returned no data or an empty list."); }
                return Result<List<FmpQuoteDto>>.Success(quotes);
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                _logger.LogWarning(ex, "FMP API returned '402 Payment Required' for the bulk crypto endpoint. This is a free-tier limitation."); return Result<List<FmpQuoteDto>>.Failure("This fallback data source requires a premium API key.");
            }
            catch (Exception ex) { _logger.LogError(ex, "An exception occurred while fetching the bulk crypto list from FMP API."); return Result<List<FmpQuoteDto>>.Failure($"FMP API error: {ex.Message}"); }
        }
    }
}