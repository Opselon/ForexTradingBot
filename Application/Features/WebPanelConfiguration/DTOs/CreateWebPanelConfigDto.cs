using System.ComponentModel.DataAnnotations; // For potential validation attributes

namespace Application.Features.WebPanelConfiguration.DTOs
{
    public class CreateWebPanelConfigDto
    {
        [Required]
        [MaxLength(100)]
        public string? SiteTitle { get; set; }

        [MaxLength(50)]
        public string? DefaultTheme { get; set; }

        [Range(10, 1000)]
        public int MaxLogEntriesToDisplay { get; set; }

        public bool IsFeatureXEnabled { get; set; }
        public bool IsFeatureYEnabled { get; set; }

        [EmailAddress]
        [MaxLength(100)]
        public string? ContactEmail { get; set; }

        [Range(1, 100)]
        public int DefaultPageSize { get; set; }

        public bool IsActive { get; set; } // Client might suggest if this new one should be active

        // Connection settings
        [DataType(DataType.Password)]
        public string? TelegramBotToken { get; set; }

        [DataType(DataType.Password)]
        public string? DatabaseConnectionString { get; set; }

        public string? RedisConnectionString { get; set; }

        // Properties from settings.html
        [MaxLength(100)]
        public string? AppName { get; set; }
        public bool MaintenanceMode { get; set; }

        [RegularExpression(@"^\d+$", ErrorMessage = "Admin Chat ID must be numeric.")]
        public string? AdminChatId { get; set; }

        [Range(5, 1440)] // 5 minutes to 24 hours
        public int SessionTimeoutMinutes { get; set; }
    }
}
