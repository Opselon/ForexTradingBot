// File: Application/Interfaces/ICloudflareRadarService.cs
using Shared.Results;

namespace Application.Interfaces
{
    #region DTOs (Data Transfer Objects) - Upgraded for Real Data

    /// <summary>
    /// Represents the Internet Quality Index (IQI) data.
    /// </summary>
    public record IqiData(double Value, string Rating, DateTime Timestamp);

    /// <summary>
    /// Represents traffic anomaly data.
    /// </summary>
    public record TrafficData(double PercentageChange, string ChangeDirection, DateTime Timestamp);

    /// <summary>
    /// Represents Layer 7 DDoS attack data.
    /// </summary>
    public record AttackData(string TopSourceCountry, double PercentageOfTotal, DateTime Timestamp);

    /// <summary>
    /// Represents the distribution of traffic by HTTP protocol version.
    /// </summary>
    public record HttpProtocolData(double Http2, double Http3, DateTime Timestamp);

    /// <summary>
    /// Represents the distribution of traffic by device type.
    /// </summary>
    public record DeviceTypeData(double Desktop, double Mobile, DateTime Timestamp);

    /// <summary>
    /// Represents the distribution of traffic between bots and humans.
    /// </summary>
    public record BotTrafficData(double Bot, double Human, DateTime Timestamp);

    /// <summary>
    /// The main, consolidated report DTO, containing all fetched data points for a country.
    /// </summary>
    public record CloudflareCountryReportDto
    {
        public required string CountryCode { get; init; }
        public required string CountryName { get; init; }
        public required string RadarUrl { get; init; }
        public required string ReportImageUrl { get; init; }

        // --- Upgraded Data Fields ---
        public IqiData? InternetQuality { get; init; }
        public TrafficData? TrafficAnomalies { get; init; }
        public AttackData? Layer7Attacks { get; init; }
        public HttpProtocolData? HttpProtocolDistribution { get; init; }
        public DeviceTypeData? DeviceTypeDistribution { get; init; }
        public BotTrafficData? BotVsHumanTraffic { get; init; }
    }
    #endregion

    /// <summary>
    /// Defines the contract for a service that fetches and consolidates
    /// internet health and security data from Cloudflare's Radar.
    /// </summary>
    public interface ICloudflareRadarService
    {
        /// <summary>
        /// Asynchronously fetches a comprehensive report for a specific country.
        /// </summary>
        /// <param name="countryCode">The ISO 3166-1 Alpha-2 code for the country (e.g., "US", "DE").</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result contains
        /// a Result object which, on success, holds a <see cref="CloudflareCountryReportDto"/>.
        /// </returns>
        Task<Result<CloudflareCountryReportDto>> GetCountryReportAsync(string countryCode, CancellationToken cancellationToken);
    }
}