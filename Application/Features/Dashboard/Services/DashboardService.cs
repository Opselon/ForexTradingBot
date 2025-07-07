using Application.Common.Interfaces;
using Application.Features.Dashboard.DTOs;
using Application.Features.Dashboard.Interfaces;
using Domain.Entities; // Required for User, Signal etc.
using Microsoft.EntityFrameworkCore; // For CountAsync, ToListAsync etc.
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Application.Features.Dashboard.Services
{
    public class DashboardService : IDashboardService
    {
        private readonly IUserRepository _userRepository;
        private readonly ISignalRepository _signalRepository;
        private readonly IAppDbContext _dbContext; // For more complex queries or direct access
        private readonly ILogger<DashboardService> _logger;
        // private readonly ITransactionRepository _transactionRepository; // If needed for revenue

        public DashboardService(
            IUserRepository userRepository,
            ISignalRepository signalRepository,
            IAppDbContext dbContext,
            ILogger<DashboardService> logger
            // ITransactionRepository transactionRepository
            )
        {
            _userRepository = userRepository;
            _signalRepository = signalRepository;
            _dbContext = dbContext;
            _logger = logger;
            // _transactionRepository = transactionRepository;
        }

        public async Task<DashboardDataDto> GetDashboardDataAsync()
        {
            _logger.LogInformation("Gathering dashboard data.");
            var dashboardData = new DashboardDataDto();

            try
            {
                // --- Stats ---
                dashboardData.TotalUsers = await GetTotalUsersStatAsync();
                dashboardData.SignalsToday = await GetSignalsTodayStatAsync();
                dashboardData.RevenueToday = GetRevenueTodayStat(); // Mocked/Placeholder
                dashboardData.ErrorsToday = GetErrorsTodayStat();   // Mocked/Placeholder

                // --- User Growth Chart ---
                dashboardData.UserGrowthChart = await GetUserGrowthChartDataAsync();

                // --- Activity Feed ---
                dashboardData.ActivityFeed = GetActivityFeedData(); // Mocked

                // --- System Health ---
                dashboardData.SystemHealth = GetSystemHealthData(); // Mocked

                // --- Recent Logs ---
                dashboardData.RecentLogs = GetRecentLogsData(); // Mocked/Placeholder
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error gathering dashboard data.");
                // Return partially filled DTO or a DTO indicating an error state
                // For now, it will return whatever was populated before the error
            }

            return dashboardData;
        }

        private async Task<DashboardStatItemDto> GetTotalUsersStatAsync()
        {
            try
            {
                // Assuming IUserRepository has a GetTotalCountAsync or similar,
                // or we can use IAppDbContext if not.
                var totalCount = await _dbContext.Users.CountAsync();

                // Trend: Compare with users count from yesterday or last 7 days.
                // This requires more historical data or snapshots. For now, mock trend.
                var usersYesterday = await _dbContext.Users.CountAsync(u => u.CreatedAt < DateTime.UtcNow.Date);
                var newUsersToday = totalCount - usersYesterday;
                string trend = newUsersToday >= 0 ? $"+{newUsersToday}" : newUsersToday.ToString();

                return new DashboardStatItemDto(
                    value: totalCount.ToString(),
                    trendValue: trend,
                    isPositiveTrend: newUsersToday >= 0,
                    label: "Total Users"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching total users stat.");
                return new DashboardStatItemDto(label: "Total Users", value:"Error");
            }
        }

        private async Task<DashboardStatItemDto> GetSignalsTodayStatAsync()
        {
            try
            {
                var todayStart = DateTime.UtcNow.Date;
                var signalsTodayCount = await _dbContext.Signals
                                               .CountAsync(s => s.PublishedAt >= todayStart);

                // Trend: Compare with signals count from yesterday.
                var yesterdayStart = todayStart.AddDays(-1);
                var signalsYesterdayCount = await _dbContext.Signals
                                                   .CountAsync(s => s.PublishedAt >= yesterdayStart && s.PublishedAt < todayStart);

                string trend;
                bool isPositive = signalsTodayCount >= signalsYesterdayCount;
                if (signalsYesterdayCount == 0 && signalsTodayCount > 0) trend = "+Inf"; // Or simply +N
                else if (signalsYesterdayCount == 0 && signalsTodayCount == 0) trend = "0";
                else trend = $"{(signalsTodayCount - signalsYesterdayCount) * 100.0 / signalsYesterdayCount:F1}%";

                trend = (isPositive ? "+" : "") + trend;


                return new DashboardStatItemDto(
                    value: signalsTodayCount.ToString(),
                    trendValue: trend, // Placeholder trend
                    isPositiveTrend: isPositive,
                    label: "Signals Today"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching signals today stat.");
                return new DashboardStatItemDto(label: "Signals Today", value: "Error");
            }
        }

        private DashboardStatItemDto GetRevenueTodayStat() // Placeholder
        {
            // Actual implementation would involve querying transactions or payment events.
            return new DashboardStatItemDto(value: "$0.00", trendValue: "N/A", label: "Revenue Today");
        }

        private DashboardStatItemDto GetErrorsTodayStat() // Placeholder
        {
            // Actual implementation would involve querying a logging system or error table.
            return new DashboardStatItemDto(value: "0", trendValue: "N/A", label: "Errors Today");
        }

        private async Task<List<ChartDataPointDto>> GetUserGrowthChartDataAsync()
        {
            // Get user registrations for the last 7 days
            var chartData = new List<ChartDataPointDto>();
            var today = DateTime.UtcNow.Date;
            for (int i = 6; i >= 0; i--)
            {
                var date = today.AddDays(-i);
                var nextDate = date.AddDays(1);
                var count = await _dbContext.Users
                                   .CountAsync(u => u.CreatedAt >= date && u.CreatedAt < nextDate);
                chartData.Add(new ChartDataPointDto { Label = date.ToString("MMM d"), Value = count });
            }
            return chartData;
        }

        private List<ActivityFeedItemDto> GetActivityFeedData() // Mocked
        {
            return new List<ActivityFeedItemDto>
            {
                new ActivityFeedItemDto { IconCssClass = "fas fa-user-plus", IconBackgroundColor = "#198754", Text = "New user <strong>demo_user</strong> registered.", Timestamp = DateTime.UtcNow.AddMinutes(-5), TimeAgo = "5m ago" },
                new ActivityFeedItemDto { IconCssClass = "fas fa-rss", IconBackgroundColor = "#0d6efd", Text = "Signal sent: <strong>EUR/USD Sell @ 1.0850</strong>", Timestamp = DateTime.UtcNow.AddMinutes(-15), TimeAgo = "15m ago" },
                new ActivityFeedItemDto { IconCssClass = "fas fa-cog", IconBackgroundColor = "#ffc107", Text = "Setting updated: <strong>Max Trades per Day</strong> to 10.", Timestamp = DateTime.UtcNow.AddHours(-1), TimeAgo = "1h ago" },
                new ActivityFeedItemDto { IconCssClass = "fas fa-exclamation-triangle", IconBackgroundColor = "#dc3545", Text = "<strong>Critical Error:</strong> Payment gateway timeout.", Timestamp = DateTime.UtcNow.AddHours(-2), TimeAgo = "2h ago" }
            };
        }

        private List<SystemHealthComponentDto> GetSystemHealthData() // Mocked
        {
            // In a real scenario, these would involve actual checks:
            // - API: Ping self, check critical endpoints.
            // - Database: Try a simple query.
            // - Job Scheduler (Hangfire): Check Hangfire stats API.
            // - Telegram API: Send a test message or getMe.
            return new List<SystemHealthComponentDto>
            {
                new SystemHealthComponentDto { Name = "API Service", Status = "Operational", StatusCssClass="ok", IconCssClass = "fas fa-server" },
                new SystemHealthComponentDto { Name = "Database", Status = "Connected", StatusCssClass="ok", IconCssClass = "fas fa-database" },
                new SystemHealthComponentDto { Name = "Job Scheduler", Status = "Running", StatusCssClass="ok", IconCssClass = "fas fa-tasks" },
                new SystemHealthComponentDto { Name = "Telegram API", Status = "Degraded", StatusCssClass="degraded", IconCssClass = "fab fa-telegram" },
                new SystemHealthComponentDto { Name = "Redis Cache", Status = "Operational", StatusCssClass="ok", IconCssClass = "fas fa-memory" }
            };
        }

        private List<string> GetRecentLogsData() // Mocked/Placeholder
        {
            // Actual implementation would fetch from a logging sink (e.g., a database table, Seq, etc.)
            // This is highly dependent on how logging is stored and accessed.
            return new List<string>
            {
                $"[{DateTime.UtcNow.AddMinutes(-1):HH:mm:ss}] [INFO] User 'admin' logged in.",
                $"[{DateTime.UtcNow.AddMinutes(-3):HH:mm:ss}] [WARN] Signal 'BTC/USD' processing delayed.",
                $"[{DateTime.UtcNow.AddMinutes(-5):HH:mm:ss}] [INFO] Scheduled job 'DailyReportJob' completed.",
                $"[{DateTime.UtcNow.AddMinutes(-10):HH:mm:ss}] [ERROR] Failed to process payment for user 'test_user_123'."
            };
        }
    }
}
