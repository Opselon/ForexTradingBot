using Domain.Common;

namespace Domain.Entities
{
    public class WebPanelConfig : BaseEntity<int>
    {
        public string? SiteTitle { get; set; }

        public string? DefaultTheme { get; set; } // e.g., "dark", "light"

        public int MaxLogEntriesToDisplay { get; set; }

        public bool IsFeatureXEnabled { get; set; }

        public bool IsFeatureYEnabled { get; set; }

        public string? ContactEmail { get; set; }

        public int DefaultPageSize { get; set; }

        // For potential future use if multiple configs are stored,
        // though the initial plan implies a single active one.
        public bool IsActive { get; set; }

        public DateTime LastModifiedAt { get; set; }

        // Connection settings from config.html
        public string? TelegramBotToken { get; set; } // Sensitive
        public string? DatabaseConnectionString { get; set; } // Sensitive
        public string? RedisConnectionString { get; set; } // Optional

        // Settings from settings.html
        public string? AppName { get; set; }
        public bool MaintenanceMode { get; set; }
        public string? AdminChatId { get; set; } // For admin notifications
        public int SessionTimeoutMinutes { get; set; }


        // Constructor to set defaults if necessary
        public WebPanelConfig()
        {
            SiteTitle = "Forex Trading Bot Admin Panel";
            DefaultTheme = "dark";
            MaxLogEntriesToDisplay = 100;
            IsFeatureXEnabled = true;
            IsFeatureYEnabled = false;
            ContactEmail = "admin@example.com";
            DefaultPageSize = 20;
            IsActive = true;
            LastModifiedAt = DateTime.UtcNow;
            // Initialize connection strings to null or empty
            TelegramBotToken = null;
            DatabaseConnectionString = null;
            RedisConnectionString = null;

            // Initialize new settings.html fields
            AppName = "Forex Trading Bot";
            MaintenanceMode = false;
            AdminChatId = null; // Needs to be configured
            SessionTimeoutMinutes = 60;
        }
    }
}
