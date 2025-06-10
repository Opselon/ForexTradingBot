// File: Application/DTOs/Fred/FredReleasesResponseDto.cs
using System.Text.Json.Serialization;

namespace Application.DTOs.Fred
{
    public class FredReleasesResponseDto
    {
        [JsonPropertyName("realtime_start")]
        public string RealtimeStart { get; set; } = string.Empty;

        [JsonPropertyName("realtime_end")]
        public string RealtimeEnd { get; set; } = string.Empty;

        [JsonPropertyName("order_by")]
        public string OrderBy { get; set; } = string.Empty;

        [JsonPropertyName("sort_order")]
        public string SortOrder { get; set; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("offset")]
        public int Offset { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("releases")]
        public List<FredReleaseDto> Releases { get; set; } = new();
    }
}