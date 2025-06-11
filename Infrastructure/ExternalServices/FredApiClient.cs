// File: Infrastructure/ExternalServices/FredApiClient.cs
using Application.Common.Interfaces.Fred;
using Application.DTOs.Fred;
using Microsoft.Extensions.Logging;
using Shared.Results;
using System.Net.Http.Json;

namespace Infrastructure.ExternalServices
{
    public class FredApiClient : IFredApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<FredApiClient> _logger;
        private readonly string _apiKey;

        public FredApiClient(HttpClient httpClient, ILogger<FredApiClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            // VVVVVV THE PRIMARY FIX IS HERE VVVVVV
            // The public demonstration API key is now hardcoded directly.
            // This removes the dependency on IConfiguration for the key.
            _apiKey = "5e7fd1c1209649f37da8325a2ef67c4a";
            // ^^^^^^ END OF THE PRIMARY FIX ^^^^^^

            _httpClient.BaseAddress = new Uri("https://api.stlouisfed.org/fred/");
        }

        public async Task<Result<FredReleaseTablesResponseDto>> GetReleaseTablesAsync(int releaseId, int? elementId = null, CancellationToken cancellationToken = default)
        {
            var requestUri = $"release/tables?release_id={releaseId}&api_key={_apiKey}&file_type=json";
            if (elementId.HasValue)
            {
                requestUri += $"&element_id={elementId.Value}";
            }

            try
            {
                var response = await _httpClient.GetFromJsonAsync<FredReleaseTablesResponseDto>(requestUri, cancellationToken);
                return response == null
                    ? Result<FredReleaseTablesResponseDto>.Failure("Failed to deserialize release tables response from FRED API.")
                    : Result<FredReleaseTablesResponseDto>.Success(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching release tables from FRED API for ReleaseID {ReleaseId}", releaseId);
                return Result<FredReleaseTablesResponseDto>.Failure($"An error occurred: {ex.Message}");
            }
        }

        public async Task<Result<FredSeriesSearchResponseDto>> SearchEconomicSeriesAsync(string searchText, int limit = 10, CancellationToken cancellationToken = default)
        {
            var encodedSearchText = System.Net.WebUtility.UrlEncode(searchText);
            var requestUri = $"series/search?api_key={_apiKey}&search_text={encodedSearchText}&file_type=json&limit={limit}";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<FredSeriesSearchResponseDto>(requestUri, cancellationToken);
                return response == null
                    ? Result<FredSeriesSearchResponseDto>.Failure("Failed to deserialize series search response from FRED API.")
                    : Result<FredSeriesSearchResponseDto>.Success(response);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request to FRED series search failed with status {StatusCode}. Search: '{SearchText}'", ex.StatusCode, searchText);
                return Result<FredSeriesSearchResponseDto>.Failure($"API request failed: {ex.StatusCode}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for economic series on FRED API. Search: '{SearchText}'", searchText);
                return Result<FredSeriesSearchResponseDto>.Failure($"An error occurred while searching for data: {ex.Message}");
            }
        }

        public async Task<Result<FredReleasesResponseDto>> GetEconomicReleasesAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
        {
            // Now that _apiKey is guaranteed to exist, we can build the URI simply.
            var requestUri = $"releases?api_key={_apiKey}&file_type=json&limit={limit}&offset={offset}";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<FredReleasesResponseDto>(requestUri, cancellationToken);
                return response == null
                    ? Result<FredReleasesResponseDto>.Failure("Failed to deserialize response from FRED API.")
                    : Result<FredReleasesResponseDto>.Success(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching economic releases from FRED API.");
                return Result<FredReleasesResponseDto>.Failure($"An error occurred while fetching data: {ex.Message}");
            }
        }
    }
}