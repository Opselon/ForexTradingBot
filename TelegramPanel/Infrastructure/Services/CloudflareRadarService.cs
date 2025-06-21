using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Shared.Results;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Services
{
    /// <summary>
    /// A high-performance service to fetch real-time internet insights from the Cloudflare Radar API.
    /// This implementation uses a hardcoded, public API token as requested.
    /// </summary>
    public class CloudflareRadarService : ICloudflareRadarService
    {
        private readonly ILogger<CloudflareRadarService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        // --- API Configuration ---
        private const string ApiToken = "iarYYgFtNTOxBGoPUPd_TYoQ4L5p4xfqOdzKt-pH";
        private const string BaseUrl = "https://api.cloudflare.com/client/v4";

        public CloudflareRadarService(ILogger<CloudflareRadarService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<Result<CloudflareCountryReportDto>> GetCountryReportAsync(string countryCode, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching LIVE Cloudflare Radar report for country: {CountryCode}", countryCode);
            if (string.IsNullOrWhiteSpace(countryCode))
                return Result<CloudflareCountryReportDto>.Failure("Country code cannot be empty.");

            try
            {
                // --- Fetch all data points in parallel for maximum speed ---
                var dateRange = "7d";
                var anomaliesDateRange = "48h"; // Use a shorter range as suggested by Cloudflare for anomalies

                // CORRECTED API Calls: Added 'metric' parameters as required by Cloudflare Radar API
                // And split http/summary into distinct metric calls.
                var iqiTask = GetFromApiAsync<IqiSummaryPayload>($"/radar/quality/iqi/summary?location={countryCode}&dateRange={dateRange}&metric=latency", cancellationToken);
                var anomaliesTask = GetFromApiAsync<TrafficAnomaliesSummaryPayload>($"/radar/traffic_anomalies/summary?location={countryCode}&dateRange={anomaliesDateRange}&metric=bandwidth", cancellationToken);
                var attacksTask = GetFromApiAsync<AttackSummaryPayload>($"/radar/attacks/layer7/summary?location={countryCode}&dateRange={dateRange}&metric=requests", cancellationToken);
                var locationTask = GetFromApiAsync<LocationPayload>($"/radar/locations/{countryCode.ToUpper()}", cancellationToken);

                // These metrics from /http/summary need separate calls with different 'metric' parameters
                var httpProtocolTask = GetFromApiAsync<HttpProtocolApiResultPayload>($"/radar/http/summary?location={countryCode}&dateRange={dateRange}&metric=http_protocol", cancellationToken);
                var deviceTypeTask = GetFromApiAsync<DeviceTypeApiResultPayload>($"/radar/http/summary?location={countryCode}&dateRange={dateRange}&metric=device_type", cancellationToken);
                var botHumanTask = GetFromApiAsync<BotTrafficApiResultPayload>($"/radar/http/summary?location={countryCode}&dateRange={dateRange}&metric=bot_class", cancellationToken);

                await Task.WhenAll(
                    iqiTask, anomaliesTask, attacksTask, locationTask,
                    httpProtocolTask, deviceTypeTask, botHumanTask
                );

                // --- Consolidate results into the final DTO ---
                var iqiResult = iqiTask.Result;
                var anomalyResult = anomaliesTask.Result;
                var attackResult = attacksTask.Result;
                var locationResult = locationTask.Result;
                var httpProtocolResult = httpProtocolTask.Result;
                var deviceTypeResult = deviceTypeTask.Result;
                var botHumanResult = botHumanTask.Result;

                // Cloudflare API doesn't always provide a top-level timestamp for summary data,
                // so we'll use UtcNow or the timestamp from a specific result if available.
                var reportTimestamp = iqiResult?.Timestamp != default ? iqiResult.Timestamp : DateTime.UtcNow;

                var report = new CloudflareCountryReportDto
                {
                    CountryCode = countryCode.ToUpper(),
                    CountryName = locationResult?.Name ?? countryCode.ToUpper(),
                    RadarUrl = $"https://radar.cloudflare.com/locations/{countryCode.ToLower()}",
                    ReportImageUrl = "https://i.postimg.cc/k4KSy2pS/cloudflare-radar-generic.png",

                    InternetQuality = iqiResult != null ? new IqiData(iqiResult.Value, iqiResult.Rating, iqiResult.Timestamp) : null,
                    TrafficAnomalies = anomalyResult != null ? new TrafficData(anomalyResult.Percentage, anomalyResult.Direction, anomalyResult.Timestamp) : null,
                    Layer7Attacks = attackResult != null ? new AttackData(attackResult.TopOriginCountryName, attackResult.Percentage, attackResult.Timestamp) : null,

                    // Map the results from specific HTTP metric calls
                    HttpProtocolDistribution = httpProtocolResult != null ? new HttpProtocolData(
                        httpProtocolResult.Dimensions.FirstOrDefault(d => d.Protocol == "HTTP/2")?.Requests ?? 0,
                        httpProtocolResult.Dimensions.FirstOrDefault(d => d.Protocol == "HTTP/3")?.Requests ?? 0,
                        reportTimestamp) : null,
                    DeviceTypeDistribution = deviceTypeResult != null ? new DeviceTypeData(
                        deviceTypeResult.Dimensions.FirstOrDefault(d => d.DeviceType == "desktop")?.Requests ?? 0,
                        deviceTypeResult.Dimensions.FirstOrDefault(d => d.DeviceType == "mobile")?.Requests ?? 0,
                        reportTimestamp) : null,
                    BotVsHumanTraffic = botHumanResult != null ? new BotTrafficData(
                        botHumanResult.Dimensions.FirstOrDefault(d => d.BotClass == "bot")?.Requests ?? 0,
                        botHumanResult.Dimensions.FirstOrDefault(d => d.BotClass == "human")?.Requests ?? 0,
                        reportTimestamp) : null
                };

                _logger.LogInformation("Successfully fetched and compiled live report for {CountryCode}", countryCode);
                return Result<CloudflareCountryReportDto>.Success(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while fetching Cloudflare Radar data for {CountryCode}", countryCode);
                // Return a failure result with specific error message
                return Result<CloudflareCountryReportDto>.Failure($"API call failed: {ex.Message}");
            }
        }

        private async Task<T?> GetFromApiAsync<T>(string endpoint, CancellationToken cancellationToken) where T : class
        {
            var client = _httpClientFactory.CreateClient("Cloudflare"); // Use a named client for best practice
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            var url = $"{BaseUrl}{endpoint}";

            try
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Cloudflare API call to {Url} failed with status {StatusCode}. Response: {ErrorContent}", url, response.StatusCode, errorContent);
                    return null;
                }

                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                // Deserialize into the generic wrapper and extract the 'result' payload
                var apiResponse = await JsonSerializer.DeserializeAsync<ApiResponse<T>>(contentStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                return apiResponse?.Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during Cloudflare API call to {Url}", url);
                return null;
            }
        }

        #region API Response Models (Payloads for GetFromApiAsync)

        // Generic wrapper for all Cloudflare API responses
        private record ApiResponse<T>
        {
            [JsonPropertyName("result")]
            public T? Result { get; init; }

            [JsonPropertyName("success")]
            public bool Success { get; init; }

            [JsonPropertyName("errors")]
            public JsonError[]? Errors { get; init; }
        }

        private record JsonError(int Code, string Message);


        // Specific payload models for each endpoint (renamed to Payload for clarity)
        private record IqiSummaryPayload { public double Value { get; init; } public string Rating { get; init; } = ""; public DateTime Timestamp { get; init; } }
        private record TrafficAnomaliesSummaryPayload { [property: JsonPropertyName("pool_percentage")] public double Percentage { get; init; } public string Direction { get; init; } = ""; public DateTime Timestamp { get; init; } }
        private record AttackSummaryPayload { [property: JsonPropertyName("top_origin_country_name")] public string TopOriginCountryName { get; init; } = ""; [property: JsonPropertyName("percentage_of_total")] public double Percentage { get; init; } public DateTime Timestamp { get; init; } }
        private record LocationPayload { public string Name { get; init; } = ""; }

        // New specific payload models for /radar/http/summary with different 'metric' parameters
        private record HttpProtocolApiResultPayload
        {
            [JsonPropertyName("dimensions")]
            public IEnumerable<HttpProtocolDimension> Dimensions { get; init; } = Enumerable.Empty<HttpProtocolDimension>();
            // Cloudflare API might have a 'date' or 'until' field here for timestamp
            // For simplicity, we'll rely on the main reportTimestamp or UtcNow
            // public DateTime? Date { get; init; }
        }
        private record HttpProtocolDimension([property: JsonPropertyName("protocol")] string Protocol, [property: JsonPropertyName("requests")] double Requests);

        private record DeviceTypeApiResultPayload
        {
            [JsonPropertyName("dimensions")]
            public IEnumerable<DeviceTypeDimension> Dimensions { get; init; } = Enumerable.Empty<DeviceTypeDimension>();
            // public DateTime? Date { get; init; }
        }
        private record DeviceTypeDimension([property: JsonPropertyName("deviceType")] string DeviceType, [property: JsonPropertyName("requests")] double Requests);

        private record BotTrafficApiResultPayload
        {
            [JsonPropertyName("dimensions")]
            public IEnumerable<BotTrafficDimension> Dimensions { get; init; } = Enumerable.Empty<BotTrafficDimension>();
            // public DateTime? Date { get; init; }
        }
        private record BotTrafficDimension([property: JsonPropertyName("botClass")] string BotClass, [property: JsonPropertyName("requests")] double Requests);

        // REMOVED: The old HttpSummary record is no longer needed as its data is now split.
        #endregion
    }
}