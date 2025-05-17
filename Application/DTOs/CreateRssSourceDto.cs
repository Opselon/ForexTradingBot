using System;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class CreateRssSourceDto
    {
        [Required]
        [Url]
        [StringLength(500)]
        public string Url { get; set; } = string.Empty;

        [Required]
        [StringLength(150)]
        public string SourceName { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
        public string? Description { get; set; }
        public int? FetchIntervalMinutes { get; set; }
        public Guid? DefaultSignalCategoryId { get; set; }
        public string? ETag { get; set; }
    }
}