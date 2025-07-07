namespace Application.Features.Dashboard.DTOs
{
    public class DashboardStatItemDto
    {
        public string Value { get; set; } = "--"; // e.g., "1,234" or "$500.00"
        public string TrendValue { get; set; } = "--"; // e.g., "+5.2%", "-10", "N/A"
        public bool IsPositiveTrend { get; set; } = true; // To determine icon (up/down arrow) and color
        public string Label { get; set; } = string.Empty; // e.g. "Total Users" - though label is static in HTML

        public DashboardStatItemDto(string value = "--", string trendValue = "--", bool isPositiveTrend = true, string label = "")
        {
            Value = value;
            TrendValue = trendValue;
            IsPositiveTrend = isPositiveTrend;
            Label = label;
        }
    }
}
