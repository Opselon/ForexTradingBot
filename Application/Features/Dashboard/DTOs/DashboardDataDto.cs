using System.Collections.Generic;

namespace Application.Features.Dashboard.DTOs
{
    public class DashboardDataDto
    {
        public DashboardStatItemDto TotalUsers { get; set; } = new DashboardStatItemDto();
        public DashboardStatItemDto SignalsToday { get; set; } = new DashboardStatItemDto();
        public DashboardStatItemDto RevenueToday { get; set; } = new DashboardStatItemDto(label: "Revenue Today", trendValue: "N/A"); // Placeholder
        public DashboardStatItemDto ErrorsToday { get; set; } = new DashboardStatItemDto();

        public List<ChartDataPointDto> UserGrowthChart { get; set; } = new List<ChartDataPointDto>();
        // public List<ChartDataPointDto> SignalsPerDayChart { get; set; } // Example for another chart if needed

        public List<ActivityFeedItemDto> ActivityFeed { get; set; } = new List<ActivityFeedItemDto>();
        public List<SystemHealthComponentDto> SystemHealth { get; set; } = new List<SystemHealthComponentDto>();
        public List<string> RecentLogs { get; set; } = new List<string>(); // Simple list of log strings

        public DashboardDataDto()
        {
            // Initialize with default/empty values to avoid nulls on client if some data isn't ready
            TotalUsers = new DashboardStatItemDto(label: "Total Users");
            SignalsToday = new DashboardStatItemDto(label: "Signals Today");
            RevenueToday = new DashboardStatItemDto(label: "Revenue Today", trendValue: "N/A");
            ErrorsToday = new DashboardStatItemDto(label: "Errors Today");
        }
    }
}
