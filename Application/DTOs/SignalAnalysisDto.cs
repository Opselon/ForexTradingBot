using System;

namespace Application.DTOs
{
    public class SignalAnalysisDto
    {
        public Guid Id { get; set; }
        public Guid SignalId { get; set; }
        public string AnalystName { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}