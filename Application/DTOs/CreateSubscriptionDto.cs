using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object for creating a new subscription.
    /// </summary>
    public class CreateSubscriptionDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the user for whom the subscription is being created.
        /// </summary>
        [Required]
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the start date of the subscription.
        /// </summary>
        [Required]
        public DateTime StartDate { get; set; }

        /// <summary>
        /// Gets or sets the end date of the subscription.
        /// </summary>
        [Required]
        public DateTime EndDate { get; set; }

        // /// <summary>
        // /// Gets or sets the unique identifier of the subscription plan.
        // /// Uncomment and use if you have different subscription plans.
        // /// </summary>
        // [Required]
        // public Guid PlanId { get; set; } // اگر پلن‌های اشتراک دارید

        #endregion
    }
}