using System;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class CreateSubscriptionDto
    {
        [Required]
        public Guid UserId { get; set; }

        [Required]
        public DateTime StartDate { get; set; }

        [Required]
        public DateTime EndDate { get; set; }

        // [Required]
        // public Guid PlanId { get; set; } // اگر پلن‌های اشتراک دارید
    }
}