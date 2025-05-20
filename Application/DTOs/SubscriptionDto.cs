namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object representing a user's subscription information.
    /// This DTO is used to transfer subscription data between layers of the application.
    /// </summary>
    public class SubscriptionDto
    {
        #region Properties

        /// <summary>
        /// Unique identifier for the subscription
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Unique identifier of the user who owns this subscription
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// The date when the subscription begins
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// The date when the subscription ends
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// Indicates whether the subscription is currently active
        /// This value should be calculated and set in the Application layer
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// The date and time when the subscription record was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        // public string PlanName { get; set; } // If connected to a subscription plan

        #endregion
    }
}