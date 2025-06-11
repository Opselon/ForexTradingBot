// File: Application/DTOs/Fred/FredReleaseTablesResponseDto.cs
using System.Text.Json.Serialization;

namespace Application.DTOs.Fred
{
    public class FredReleaseTablesResponseDto
    {
        [JsonPropertyName("elements")]
        public List<FredReleaseTableElementDto> Elements { get; set; } = new();
    }
}