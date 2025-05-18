namespace Application.DTOs
{
    public class SubscriptionDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public bool IsActive { get; set; } // این مقدار باید در لایه Application محاسبه و پر شود
        public DateTime CreatedAt { get; set; }
        // public string PlanName { get; set; } // اگر به پلن اشتراک متصل هستید
    }
}