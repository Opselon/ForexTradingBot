using Microsoft.Extensions.Logging;
using Shared.Results;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    public class CloudflareRadarService : ICloudflareRadarService
    {
        private readonly ILogger<CloudflareRadarService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private const string ApiToken = "iarYYgFtNTOxBGoPUPd_TYoQ4L5p4xfqOdzKt-pH";
        private const string BaseUrl = "https://api.cloudflare.com/client/v4";

        public CloudflareRadarService(ILogger<CloudflareRadarService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<Result<CloudflareCountryReportDto>> GetCountryReportAsync(string countryCode, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching ULTIMATE Cloudflare Radar report for country: {CountryCode}", countryCode);
            if (string.IsNullOrWhiteSpace(countryCode))
            {
                return Result<CloudflareCountryReportDto>.Failure("Country code cannot be empty.");
            }

            try
            {
                string dateRange = "7d";
                string countryCodeUpper = countryCode.ToUpper();

                // --- EXPANDED API CALLS FOR MAX DETAIL ---
                Task<(LocationWrapper? result, Meta? meta)> locationTask = GetFromApiAsync<LocationWrapper>($"/radar/entities/locations/{countryCodeUpper}", cancellationToken);
                Task<(IqiSummaryPayloadWrapper? result, Meta? meta)> iqiTask = GetFromApiAsync<IqiSummaryPayloadWrapper>($"/radar/quality/iqi/summary?location={countryCodeUpper}&dateRange={dateRange}&metric=latency", cancellationToken);
                Task<(TopAttacksWrapper? result, Meta? meta)> l7AttacksTask = GetFromApiAsync<TopAttacksWrapper>($"/radar/attacks/layer7/top/locations/origin?location={countryCodeUpper}&dateRange={dateRange}&limit=1", cancellationToken);
                Task<(HttpProtocolApiResultPayload? result, Meta? meta)> httpProtocolTask = GetFromApiAsync<HttpProtocolApiResultPayload>($"/radar/http/summary/http_version?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(DeviceTypeApiResultPayload? result, Meta? meta)> deviceTypeTask = GetFromApiAsync<DeviceTypeApiResultPayload>($"/radar/http/summary/device_type?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(BotTrafficApiResultPayload? result, Meta? meta)> botHumanTask = GetFromApiAsync<BotTrafficApiResultPayload>($"/radar/http/summary/bot_class?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(OutagesWrapper? result, Meta? meta)> outagesTask = GetFromApiAsync<OutagesWrapper>($"/radar/annotations/outages?limit=5&dateRange={dateRange}&location={countryCodeUpper}", cancellationToken);
                Task<(AttackMitigationPayload? result, Meta? meta)> mitigationTask = GetFromApiAsync<AttackMitigationPayload>($"/radar/attacks/layer7/summary/mitigation_product?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(TlsVersionPayload? result, Meta? meta)> tlsVersionTask = GetFromApiAsync<TlsVersionPayload>($"/radar/http/summary/tls_version?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(IpVersionPayload? result, Meta? meta)> ipVersionTask = GetFromApiAsync<IpVersionPayload>($"/radar/http/summary/ip_version?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(PostQuantumPayload? result, Meta? meta)> postQuantumTask = GetFromApiAsync<PostQuantumPayload>($"/radar/http/summary/post_quantum?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(OperatingSystemPayload? result, Meta? meta)> osTask = GetFromApiAsync<OperatingSystemPayload>($"/radar/http/summary/os?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);
                Task<(L3AttackProtocolPayload? result, Meta? meta)> l3AttacksTask = GetFromApiAsync<L3AttackProtocolPayload>($"/radar/attacks/layer3/summary/protocol?location={countryCodeUpper}&dateRange={dateRange}", cancellationToken);


                await Task.WhenAll(
                    locationTask, iqiTask, l7AttacksTask, httpProtocolTask, deviceTypeTask, botHumanTask,
                    outagesTask, mitigationTask, tlsVersionTask, ipVersionTask, postQuantumTask, osTask, l3AttacksTask
                );

                (LocationWrapper? locationData, _) = locationTask.Result;
                (IqiSummaryPayloadWrapper? iqiData, _) = iqiTask.Result;
                (TopAttacksWrapper? l7AttackData, _) = l7AttacksTask.Result;
                (HttpProtocolApiResultPayload? httpProtocolData, Meta? httpMeta) = httpProtocolTask.Result;
                (DeviceTypeApiResultPayload? deviceTypeData, _) = deviceTypeTask.Result;
                (BotTrafficApiResultPayload? botHumanData, _) = botHumanTask.Result;
                (OutagesWrapper? outagesData, _) = outagesTask.Result;
                (AttackMitigationPayload? mitigationData, _) = mitigationTask.Result;
                (TlsVersionPayload? tlsData, _) = tlsVersionTask.Result;
                (IpVersionPayload? ipVersionData, _) = ipVersionTask.Result;
                (PostQuantumPayload? postQuantumData, _) = postQuantumTask.Result;
                (OperatingSystemPayload? osData, _) = osTask.Result;
                (L3AttackProtocolPayload? l3AttackData, _) = l3AttacksTask.Result;

                // --- PARSE ALL THE DATA ---
                AttackOriginPayload? topL7Attack = l7AttackData?.Top0.FirstOrDefault();
                _ = double.TryParse(topL7Attack?.Value.TrimEnd('%'), CultureInfo.InvariantCulture, out double l7AttackPercentage);
                AttackData? l7AttackDto = topL7Attack != null ? new AttackData(string.IsNullOrWhiteSpace(topL7Attack.ClientCountryName) ? "Unknown" : topL7Attack.ClientCountryName, l7AttackPercentage) : null;

                CloudflareCountryReportDto report = new()
                {
                    CountryCode = countryCodeUpper,
                    CountryName = locationData?.Location?.Name ?? countryCodeUpper,
                    RadarUrl = $"https://radar.cloudflare.com/locations/{countryCode.ToLower()}",
                    ReportTimestamp = httpMeta?.LastUpdated,
                    InternetQuality = iqiData?.Summary0 != null ? new IqiData(iqiData.Summary0.P90, iqiData.Summary0.Rating ?? "N/A") : null,
                    Layer7Attacks = l7AttackDto,
                    LatestOutage = outagesData?.Annotations.FirstOrDefault() != null ? new ConfirmedOutageData(outagesData.Annotations[0].Description, outagesData.Annotations[0].StartDate, outagesData.Annotations[0].EndDate, outagesData.Annotations[0].Outage?.OutageCause ?? "N/A") : null,
                    BotVsHumanTraffic = botHumanData?.Summary0 != null ? new BotTrafficData(ParseDouble(botHumanData.Summary0.Bot), ParseDouble(botHumanData.Summary0.Human)) : null,
                    DeviceTypeDistribution = deviceTypeData?.Summary0 != null ? new DeviceTypeData(ParseDouble(deviceTypeData.Summary0.Desktop), ParseDouble(deviceTypeData.Summary0.Mobile)) : null,
                    HttpProtocolDistribution = httpProtocolData?.Summary0 != null ? new HttpProtocolData(ParseDouble(httpProtocolData.Summary0.Http3), ParseDouble(httpProtocolData.Summary0.Http2), ParseDouble(httpProtocolData.Summary0.Http1)) : null,
                    AttackMitigation = mitigationData?.Summary0 != null ? new AttackMitigationData(ParseDouble(mitigationData.Summary0.Waf), ParseDouble(mitigationData.Summary0.RateLimiting), ParseDouble(mitigationData.Summary0.BotManagement)) : null,
                    TlsVersionDistribution = tlsData?.Summary0 != null ? new TlsVersionData(ParseDouble(tlsData.Summary0.Tls13), ParseDouble(tlsData.Summary0.Tls12), ParseDouble(tlsData.Summary0.Tls11), ParseDouble(tlsData.Summary0.Tls10)) : null,
                    IpVersionDistribution = ipVersionData?.Summary0 != null ? new IpVersionData(ParseDouble(ipVersionData.Summary0.Ipv4), ParseDouble(ipVersionData.Summary0.Ipv6)) : null,
                    PostQuantumSupport = postQuantumData?.Summary0 != null ? new PostQuantumData(ParseDouble(postQuantumData.Summary0.Supported), ParseDouble(postQuantumData.Summary0.NotSupported)) : null,
                    OSDistribution = osData?.Summary0 != null ? new OperatingSystemData(ParseDouble(osData.Summary0.Windows), ParseDouble(osData.Summary0.MacOS), ParseDouble(osData.Summary0.Android), ParseDouble(osData.Summary0.IOS), ParseDouble(osData.Summary0.Linux)) : null,
                    L3AttackDistribution = l3AttackData?.Summary0 != null ? new Layer3AttackProtocolData(ParseDouble(l3AttackData.Summary0.Udp), ParseDouble(l3AttackData.Summary0.Tcp), ParseDouble(l3AttackData.Summary0.Icmp)) : null,
                };

                _logger.LogInformation("Successfully fetched and compiled ULTIMATE live report for {CountryCode}", countryCode);
                return Result<CloudflareCountryReportDto>.Success(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while fetching Cloudflare Radar data for {CountryCode}", countryCode);
                return Result<CloudflareCountryReportDto>.Failure($"API call failed: {ex.Message}");
            }
        }

        private double ParseDouble(string value)
        {
            return double.TryParse(value, CultureInfo.InvariantCulture, out double result) ? result : 0.0;
        }

        // --- START OF FULLY IMPLEMENTED METHOD ---
        private async Task<(T? result, Meta? meta)> GetFromApiAsync<T>(string endpoint, CancellationToken cancellationToken) where T : class
        {
            HttpClient client = _httpClientFactory.CreateClient("Cloudflare");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            string url = $"{BaseUrl}{endpoint}";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);

                // Read the response content once to use for logging and deserialization
                string rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

                // Log the raw JSON response at DEBUG level to see exactly what the API returns.
                _logger.LogDebug("RAW RESPONSE from {Url}: {RawJson}", url, rawJson);

                if (!response.IsSuccessStatusCode)
                {
                    // Use the rawJson we already read for the warning log
                    _logger.LogWarning("Cloudflare API call to {Url} failed with status {StatusCode}. Response: {ErrorContent}", url, response.StatusCode, rawJson);
                    return (null, null);
                }

                // Deserialize from the string we already read
                ApiResponse<T>? apiResponse = JsonSerializer.Deserialize<ApiResponse<T>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (apiResponse?.Success == false)
                {
                    string errorMessages = string.Join(", ", apiResponse.Errors?.Select(e => e.Message) ?? Enumerable.Empty<string>());
                    _logger.LogWarning("Cloudflare API call to {Url} was successful but returned an error status. Errors: {Errors}", url, errorMessages);
                    return (null, null);
                }
                return (apiResponse?.Result, apiResponse?.Meta);
            }
            catch (OperationCanceledException) { _logger.LogWarning("Cloudflare API call to {Url} was canceled.", url); return (null, null); }
            catch (Exception ex) { _logger.LogError(ex, "Exception during Cloudflare API call to {Url}", url); return (null, null); }
        }
        // --- END OF FULLY IMPLEMENTED METHOD ---

        #region Private API Response Models - Ultimate
        private record ApiResponse<T> { [JsonPropertyName("result")] public T? Result { get; init; } [JsonPropertyName("success")] public bool Success { get; init; } [JsonPropertyName("errors")] public JsonError[]? Errors { get; init; } [JsonPropertyName("meta")] public Meta? Meta { get; init; } }
        private record JsonError(int Code, string Message);
        private record Meta { [JsonPropertyName("last_updated")] public string? LastUpdated { get; init; } }
        private record LocationWrapper { [JsonPropertyName("location")] public LocationPayload? Location { get; init; } }
        private record LocationPayload { [JsonPropertyName("name")] public string Name { get; init; } = ""; }
        private record IqiSummaryPayloadWrapper { [JsonPropertyName("summary_0")] public IqiSummaryPayload? Summary0 { get; init; } }
        private record IqiSummaryPayload { [JsonPropertyName("p90")] public double P90 { get; init; } [JsonPropertyName("rating")] public string? Rating { get; init; } }
        private record TopAttacksWrapper { [JsonPropertyName("top_0")] public List<AttackOriginPayload> Top0 { get; init; } = []; }
        private record AttackOriginPayload { [JsonPropertyName("clientCountryName")] public string ClientCountryName { get; init; } = ""; [JsonPropertyName("value")] public string Value { get; init; } = ""; }
        private record HttpProtocolApiResultPayload { [JsonPropertyName("summary_0")] public HttpProtocolSummaryPayload? Summary0 { get; init; } }
        private record HttpProtocolSummaryPayload { [JsonPropertyName("HTTP/3")] public string Http3 { get; init; } = ""; [JsonPropertyName("HTTP/2")] public string Http2 { get; init; } = ""; [JsonPropertyName("HTTP/1.x")] public string Http1 { get; init; } = ""; }
        private record DeviceTypeApiResultPayload { [JsonPropertyName("summary_0")] public DeviceTypeSummaryPayload? Summary0 { get; init; } }
        private record DeviceTypeSummaryPayload { [JsonPropertyName("desktop")] public string Desktop { get; init; } = ""; [JsonPropertyName("mobile")] public string Mobile { get; init; } = ""; }
        private record BotTrafficApiResultPayload { [JsonPropertyName("summary_0")] public BotTrafficSummaryPayload? Summary0 { get; init; } }
        private record BotTrafficSummaryPayload { [JsonPropertyName("bot")] public string Bot { get; init; } = ""; [JsonPropertyName("human")] public string Human { get; init; } = ""; }
        private record OutagesWrapper { [JsonPropertyName("annotations")] public List<OutageAnnotationPayload> Annotations { get; init; } = []; }
        private record OutageAnnotationPayload { [JsonPropertyName("description")] public string Description { get; init; } = ""; [JsonPropertyName("startDate")] public DateTime StartDate { get; init; } [JsonPropertyName("endDate")] public DateTime? EndDate { get; init; } [JsonPropertyName("outage")] public OutageDetails? Outage { get; init; } }
        private record OutageDetails { [JsonPropertyName("outageCause")] public string OutageCause { get; init; } = ""; }
        private record AttackMitigationPayload { [JsonPropertyName("summary_0")] public MitigationSummary? Summary0 { get; init; } }
        private record MitigationSummary { [JsonPropertyName("WAF")] public string Waf { get; init; } = ""; [JsonPropertyName("RATE_LIMITING")] public string RateLimiting { get; init; } = ""; [JsonPropertyName("BOT_MANAGEMENT")] public string BotManagement { get; init; } = ""; }
        private record TlsVersionPayload { [JsonPropertyName("summary_0")] public TlsSummary? Summary0 { get; init; } }
        private record TlsSummary { [JsonPropertyName("TLSv1.3")] public string Tls13 { get; init; } = ""; [JsonPropertyName("TLSv1.2")] public string Tls12 { get; init; } = ""; [JsonPropertyName("TLSv1.1")] public string Tls11 { get; init; } = ""; [JsonPropertyName("TLSv1.0")] public string Tls10 { get; init; } = ""; }
        private record IpVersionPayload { [JsonPropertyName("summary_0")] public IpVersionSummary? Summary0 { get; init; } }
        private record IpVersionSummary { [JsonPropertyName("IPv4")] public string Ipv4 { get; init; } = ""; [JsonPropertyName("IPv6")] public string Ipv6 { get; init; } = ""; }
        private record PostQuantumPayload { [JsonPropertyName("summary_0")] public PostQuantumSummary? Summary0 { get; init; } }
        private record PostQuantumSummary { [JsonPropertyName("POST_QUANTUM")] public string Supported { get; init; } = ""; [JsonPropertyName("NOT_SUPPORTED")] public string NotSupported { get; init; } = ""; }
        private record OperatingSystemPayload { [JsonPropertyName("summary_0")] public OSSummary? Summary0 { get; init; } }
        private record OSSummary { [JsonPropertyName("Windows")] public string Windows { get; init; } = ""; [JsonPropertyName("macOS")] public string MacOS { get; init; } = ""; [JsonPropertyName("Android")] public string Android { get; init; } = ""; [JsonPropertyName("iOS")] public string IOS { get; init; } = ""; [JsonPropertyName("Linux")] public string Linux { get; init; } = ""; }
        private record L3AttackProtocolPayload { [JsonPropertyName("summary_0")] public L3ProtocolSummary? Summary0 { get; init; } }
        private record L3ProtocolSummary { [JsonPropertyName("UDP")] public string Udp { get; init; } = ""; [JsonPropertyName("TCP")] public string Tcp { get; init; } = ""; [JsonPropertyName("ICMP")] public string Icmp { get; init; } = ""; }
        #endregion
    }
}