namespace Domain.Settings
{
    public class AutoForwardSettings
    {
        public bool IsEnabled { get; set; } = true;
        public int DefaultDelay { get; set; } = 0;
        public int MaxMessagesPerMinute { get; set; } = 30;
        public bool RemoveSourceForwardHeader { get; set; } = false;
        public bool RemoveLinks { get; set; } = false;
        public bool StripFormatting { get; set; } = false;
        public string? CustomFooter { get; set; }
    }
}