// File: Application/DTOs/Fred/FredReleaseTableElementDto.cs
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Application.DTOs.Fred
{
    public class FredReleaseTableElementDto
    {
        [JsonPropertyName("element_id")]
        public int ElementId { get; set; }

        [JsonPropertyName("release_id")]
        public int ReleaseId { get; set; }

        [JsonPropertyName("parent_id")]
        public int ParentId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("series_id")]
        public string? SeriesId { get; set; }

        [JsonPropertyName("children")]
        public List<FredReleaseTableElementDto> Children { get; set; } = new();
    }
}