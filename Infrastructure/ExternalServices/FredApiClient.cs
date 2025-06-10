// File: Infrastructure/ExternalServices/FredApiClient.cs
using Application.Common.Interfaces;
using Application.DTOs.Fred;
using Microsoft.Extensions.Configuration;
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

        public async Task<Result<FredReleasesResponseDto>> GetEconomicReleasesAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
        {
            // Now that _apiKey is guaranteed to exist, we can build the URI simply.
            var requestUri = $"releases?api_key={_apiKey}&file_type=json&limit={limit}&offset={offset}";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<FredReleasesResponseDto>(requestUri, cancellationToken);
                if (response == null)
                {
                    return Result<FredReleasesResponseDto>.Failure("Failed to deserialize response from FRED API.");
                }
                return Result<FredReleasesResponseDto>.Success(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching economic releases from FRED API.");
                return Result<FredReleasesResponseDto>.Failure($"An error occurred while fetching data: {ex.Message}");
            }
        }
    }
}