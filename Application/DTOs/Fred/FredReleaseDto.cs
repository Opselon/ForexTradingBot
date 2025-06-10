// File: Application/DTOs/Fred/FredReleaseDto.cs
using System.Text.Json.Serialization;

namespace Application.DTOs.Fred
{
    public class FredReleaseDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("press_release")]
        public bool IsPressRelease { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

    }
}