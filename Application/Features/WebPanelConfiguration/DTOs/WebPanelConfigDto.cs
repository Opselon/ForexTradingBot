using System;

namespace Application.Features.WebPanelConfiguration.DTOs
{
    public class WebPanelConfigDto
    {
        public int Id { get; set; }
        public string? SiteTitle { get; set; }
        public string? DefaultTheme { get; set; }
        public int MaxLogEntriesToDisplay { get; set; }
        public bool IsFeatureXEnabled { get; set; }
        public bool IsFeatureYEnabled { get; set; }
        public string? ContactEmail { get; set; }
        public int DefaultPageSize { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastModifiedAt { get; set; }

        // Corresponding properties from config.html
        // Note: For security, sensitive data like full connection strings or tokens
        // might be omitted or masked in DTOs sent to the client unless absolutely necessary.
        // For now, we'll include them for completeness of the config panel,
        // but in a real scenario, consider how this data is exposed.
        public string? TelegramBotToken { get; set; }
        public string? DatabaseConnectionString { get; set; }
        public string? RedisConnectionString { get; set; }

        // Properties from settings.html
        public string? AppName { get; set; }
        public bool MaintenanceMode { get; set; }
        public string? AdminChatId { get; set; }
        public int SessionTimeoutMinutes { get; set; }
    }
}
