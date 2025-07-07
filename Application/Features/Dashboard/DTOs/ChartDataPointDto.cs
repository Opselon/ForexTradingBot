namespace Application.Features.Dashboard.DTOs
{
    public class ChartDataPointDto
    {
        public string Label { get; set; } = string.Empty; // e.g., Date as string, or category name
        public decimal Value { get; set; }
    }
}
