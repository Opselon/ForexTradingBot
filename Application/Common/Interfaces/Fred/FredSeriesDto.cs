// File: Application/DTOs/Fred/FredSeriesDto.cs
using System.Text.Json.Serialization;

namespace Application.Common.Interfaces.Fred
{
    /// <summary>
    /// Represents a single economic data series from the FRED API.
    /// </summary>
    public class FredSeriesDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("frequency_short")]
        public string FrequencyShort { get; set; } = string.Empty;

        [JsonPropertyName("units_short")]
        public string UnitsShort { get; set; } = string.Empty;

        [JsonPropertyName("seasonal_adjustment_short")]
        public string SeasonalAdjustmentShort { get; set; } = string.Empty;

        // Changed from DateTime to string to handle the API's non-standard date format.
        [JsonPropertyName("last_updated")]
        public string LastUpdated { get; set; } = string.Empty;
        // ^^^^^^ END OF THE FIX ^^^^^^

        [JsonPropertyName("popularity")]
        public int Popularity { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }
}