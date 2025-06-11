// File: Application/DTOs/Fred/FredReleaseTablesResponseDto.cs
using System.Text.Json.Serialization;

namespace Application.Common.Interfaces.Fred
{
    public class FredReleaseTablesResponseDto
    {
        [JsonPropertyName("elements")]
        public List<FredReleaseTableElementDto> Elements { get; set; } = [];
    }
}