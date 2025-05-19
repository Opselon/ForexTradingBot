using System.Collections.Generic;

namespace TelegramPanel.Infrastructure.Settings
{
    public class MarketDataSettings
    {
        public const string SectionName = "MarketData";
        public Dictionary<string, ProviderSettings> Providers { get; set; } = new();
        public int RetryCount { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 2;
        public int CacheDurationMinutes { get; set; } = 5;
    }

    public class ProviderSettings
    {
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public bool IsEnabled { get; set; } = true;
        public int Priority { get; set; } = 1;
    }

    public class CurrencyInfoSettings
    {
        public const string SectionName = "CurrencyInfo";
        public Dictionary<string, CurrencyDetails> Currencies { get; set; } = new();
    }

    public class CurrencyDetails
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public bool IsActive { get; set; } = true;
    }
} 