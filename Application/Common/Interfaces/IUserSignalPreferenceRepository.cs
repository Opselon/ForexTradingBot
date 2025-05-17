using Domain.Entities; // برای UserSignalPreference, User, SignalCategory

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت تنظیمات برگزیده سیگنال کاربر (UserSignalPreference).
    /// عملیات مربوط به مدیریت علاقه‌مندی‌های کاربران به دسته‌های سیگنال را تعریف می‌کند.
    /// </summary>
    public interface IUserSignalPreferenceRepository
    {
        /// <summary>
        /// یک تنظیم برگزیده خاص را بر اساس شناسه آن پیدا می‌کند.
        /// </summary>
        Task<UserSignalPreference?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام تنظیمات برگزیده (دسته‌های مورد علاقه) یک کاربر خاص را برمی‌گرداند.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تنظیمات برگزیده کاربر، همراه با اطلاعات دسته‌بندی.</returns>
        Task<IEnumerable<UserSignalPreference>> GetPreferencesByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// بررسی می‌کند که آیا یک کاربر به یک دسته‌بندی سیگنال خاص علاقه‌مند است یا خیر.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="categoryId">شناسه دسته‌بندی سیگنال.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر کاربر به دسته‌بندی علاقه‌مند باشد، در غیر این صورت false.</returns>
        Task<bool> IsUserSubscribedToCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک تنظیم برگزیده (علاقه‌مندی به یک دسته‌بندی) را برای یک کاربر اضافه می‌کند.
        /// </summary>
        /// <param name="preference">موجودیت تنظیمات برگزیده‌ای که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(UserSignalPreference preference, CancellationToken cancellationToken = default);

        /// <summary>
        /// چندین تنظیم برگزیده را به صورت یکجا برای یک کاربر اضافه می‌کند.
        /// ابتدا تنظیمات موجود را برای آن کاربر و دسته‌های مشخص شده حذف می‌کند (در صورت نیاز).
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="categoryIds">لیستی از شناسه‌های دسته‌بندی‌های مورد علاقه جدید.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task SetUserPreferencesAsync(Guid userId, IEnumerable<Guid> categoryIds, CancellationToken cancellationToken = default);


        /// <summary>
        /// یک تنظیم برگزیده را برای یک کاربر حذف می‌کند.
        /// </summary>
        /// <param name="preference">موجودیت تنظیمات برگزیده‌ای که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(UserSignalPreference preference, CancellationToken cancellationToken = default);

        /// <summary>
        /// تنظیم برگزیده یک کاربر برای یک دسته‌بندی خاص را حذف می‌کند.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="categoryId">شناسه دسته‌بندی سیگنال.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر حذف موفقیت‌آمیز بود یا رکوردی برای حذف وجود نداشت، false اگر خطایی رخ دهد.</returns>
        Task<bool> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام شناسه‌های کاربرانی را که به یک دسته‌بندی سیگنال خاص علاقه‌مند هستند، برمی‌گرداند.
        /// این متد برای ارسال نوتیفیکیشن‌های گروهی به کاربران علاقه‌مند به یک دسته مفید است.
        /// </summary>
        /// <param name="categoryId">شناسه دسته‌بندی سیگنال.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از شناسه‌های کاربران.</returns>
        Task<IEnumerable<Guid>> GetUserIdsByCategoryIdAsync(Guid categoryId, CancellationToken cancellationToken = default);
    }
}