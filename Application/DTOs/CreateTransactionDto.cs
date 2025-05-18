using Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class CreateTransactionDto
    {
        [Required]
        public Guid UserId { get; set; }

        [Required]
        public decimal Amount { get; set; }

        [Required]
        public TransactionType Type { get; set; }

        [StringLength(500)]
        public string? Description { get; set; }
    }
}