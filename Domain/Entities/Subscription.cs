// File: Domain/Entities/Subscription.cs
#region Usings
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema; // برای ForeignKey
#endregion

namespace Domain.Entities
{
    public class Subscription
    {
        #region Core Properties
        public Guid Id { get; set; }

        [Required]
        public Guid UserId { get; set; }

        //  (اختیاری اما بسیار توصیه شده) شناسه پلن اشتراکی که کاربر مشترک آن شده است.
        // public Guid PlanId { get; set; } //  این باید به یک موجودیت Plan یا یک enum PlanType اشاره کند

        //  (اختیاری) نام پلن برای نمایش سریع (می‌تواند از موجودیت Plan خوانده شود)
        // [MaxLength(100)]
        // public string? PlanName { get; set; }

        [Required]
        public DateTime StartDate { get; set; }

        [Required]
        public DateTime EndDate { get; set; }

        /// <summary>
        /// Indicates the current status of the subscription (e.g., Active, Expired, Cancelled, PendingPayment).
        /// Recommended to use an enum for this.
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = "Active"; //  مقدار پیش‌فرض یا بر اساس منطق ایجاد

        /// <summary>
        /// (Optional) Foreign key to the Transaction that activated or renewed this subscription.
        /// </summary>
        public Guid? ActivatingTransactionId { get; set; }
        #endregion

        #region Timestamps
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; } // ✅ اضافه شد
        #endregion

        #region Navigation Properties
        [ForeignKey(nameof(UserId))]
        [Required]
        public virtual User User { get; set; } = null!; // ✅ virtual

        //  (اختیاری) نویگیشن به پلن
        // [ForeignKey(nameof(PlanId))]
        // public virtual SubscriptionPlan Plan { get; set; } = null!;

        //  (اختیاری) نویگیشن به تراکنش فعال‌کننده
        // [ForeignKey(nameof(ActivatingTransactionId))]
        // public virtual Transaction? ActivatingTransaction { get; set; }
        #endregion

        #region Calculated Properties
        /// <summary>
        /// Calculated property to determine if the subscription is currently active based on dates and status.
        /// </summary>
        [NotMapped] //  این پراپرتی نباید به دیتابیس مپ شود
        public bool IsCurrentlyActive => Status.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                                        DateTime.UtcNow >= StartDate &&
                                        DateTime.UtcNow <= EndDate;
        #endregion

        #region Constructors


        public Subscription()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            Status = "Pending"; //  وضعیت اولیه می‌تواند Pending باشد تا پس از پرداخت، Active شود.
        }
        #endregion
    }
}