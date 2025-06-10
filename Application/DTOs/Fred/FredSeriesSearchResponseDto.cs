// File: Application/DTOs/Fred/FredSeriesSearchResponseDto.cs
using System.Text.Json.Serialization;

namespace Application.DTOs.Fred
{
    public class FredSeriesSearchResponseDto
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("offset")]
        public int Offset { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        // Handles the "seriess" naming quirk in the FRED API JSON
        [JsonPropertyName("seriess")]
        public List<FredSeriesDto> Series { get; set; } = new();
    }
}