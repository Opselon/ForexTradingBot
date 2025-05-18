namespace Application.DTOs
{
    public class SignalDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        public SignalCategoryDto? Category { get; set; } // ✅ این پراپرتی باید وجود داشته باشد
        public IEnumerable<SignalAnalysisDto>? Analyses { get; set; }
    }
}