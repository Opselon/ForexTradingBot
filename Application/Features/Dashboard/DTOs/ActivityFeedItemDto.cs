using System;

namespace Application.Features.Dashboard.DTOs
{
    public class ActivityFeedItemDto
    {
        public string IconCssClass { get; set; } = "fas fa-info-circle"; // e.g., "fas fa-user-plus", "fas fa-rss"
        public string IconBackgroundColor { get; set; } = "#6c757d"; // e.g., "#198754", "#0d6efd"
        public string Text { get; set; } = string.Empty; // e.g., "New user registered: <strong>john_doe_123</strong>"
        public DateTime Timestamp { get; set; }
        public string TimeAgo { get; set; } = string.Empty; // e.g., "5 minutes ago"
    }
}
