using Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class UpdateSignalDto
    {
        // فیلدهایی که قابل ویرایش هستند
        public SignalType? Type { get; set; }

        [StringLength(50)]
        public string? Symbol { get; set; }

        [Range(0.00000001, double.MaxValue)]
        public decimal? EntryPrice { get; set; }

        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }

        [StringLength(100)]
        public string? Source { get; set; }

        public Guid? CategoryId { get; set; }
    }
}