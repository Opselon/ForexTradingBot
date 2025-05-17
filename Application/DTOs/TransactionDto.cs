using System;

namespace Application.DTOs
{
    public class TransactionDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public decimal Amount { get; set; }
        public string Type { get; set; } = string.Empty; // TransactionType به صورت رشته
        public string? Description { get; set; }
        public DateTime Timestamp { get; set; }
    }
}