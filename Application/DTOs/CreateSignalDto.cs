using Domain.Enums; // برای SignalType
using System;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class CreateSignalDto
    {
        [Required]
        public SignalType Type { get; set; }

        [Required]
        [StringLength(50)]
        public string Symbol { get; set; } = string.Empty;

        [Required]
        [Range(0.00000001, double.MaxValue)]
        public decimal EntryPrice { get; set; }

        [Required]
        public decimal StopLoss { get; set; }

        [Required]
        public decimal TakeProfit { get; set; }

        [Required]
        [StringLength(100)]
        public string Source { get; set; } = string.Empty;

        [Required]
        public Guid CategoryId { get; set; }
    }
}