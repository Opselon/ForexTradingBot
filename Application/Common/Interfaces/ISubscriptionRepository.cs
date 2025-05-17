using Domain.Entities; // برای Subscription و User

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت اشتراک (Subscription).
    /// عملیات مربوط به دسترسی و مدیریت داده‌های اشتراک‌های کاربران را تعریف می‌کند.
    /// </summary>
    public interface ISubscriptionRepository
    {
        /// <summary>
        /// یک اشتراک را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه اشتراک.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت اشتراک در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام اشتراک‌های یک کاربر خاص را به صورت ناهمزمان برمی‌گرداند.
        /// </summary>
        /// <param name="userId">شناسه کاربری که اشتراک‌ها برای او جستجو می‌شوند.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از اشتراک‌های کاربر.</returns>
        Task<IEnumerable<Subscription>> GetSubscriptionsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// اشتراک فعال فعلی یک کاربر را برمی‌گرداند (اگر وجود داشته باشد).
        /// اشتراک فعال، اشتراکی است که تاریخ جاری بین تاریخ شروع و پایان آن باشد.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>اشتراک فعال کاربر یا null در صورت عدم وجود.</returns>
        Task<Subscription?> GetActiveSubscriptionByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// بررسی می‌کند که آیا یک کاربر در حال حاضر اشتراک فعال دارد یا خیر.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر کاربر اشتراک فعال دارد، در غیر این صورت false.</returns>
        Task<bool> HasActiveSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک اشتراک جدید را برای کاربر به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="subscription">موجودیت اشتراکی که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک اشتراک موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند.
        /// </summary>
        /// <param name="subscription">موجودیت اشتراکی با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک اشتراک را به صورت ناهمزمان از پایگاه داده حذف می‌کند (استفاده با احتیاط).
        /// </summary>
        /// <param name="subscription">موجودیت اشتراکی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(Subscription subscription, CancellationToken cancellationToken = default);

        // معمولاً حذف اشتراک‌ها به جای حذف فیزیکی، از طریق منقضی شدن یا تغییر وضعیت انجام می‌شود.
        // اما داشتن متد حذف برای موارد خاص مدیریتی می‌تواند مفید باشد.
    }
}