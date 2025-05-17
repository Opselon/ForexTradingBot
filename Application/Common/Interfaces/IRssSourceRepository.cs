using Domain.Entities; // برای RssSource
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت منبع RSS (RssSource).
    /// عملیات مربوط به دسترسی و مدیریت داده‌های منابع RSS را تعریف می‌کند.
    /// </summary>
    public interface IRssSourceRepository
    {
        /// <summary>
        /// یک منبع RSS را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه منبع RSS.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت منبع RSS در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<RssSource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک منبع RSS را بر اساس URL آن به صورت ناهمزمان پیدا می‌کند (URL باید منحصر به فرد باشد).
        /// </summary>
        /// <param name="url">آدرس URL منبع RSS.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت منبع RSS در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<RssSource?> GetByUrlAsync(string url, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام منابع RSS را به صورت ناهمزمان برمی‌گرداند.
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تمام منابع RSS.</returns>
        Task<IEnumerable<RssSource>> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام منابع RSS فعال را به صورت ناهمزمان برمی‌گرداند.
        /// این متد برای سرویس خواندن فیدهای RSS مفید است.
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تمام منابع RSS فعال.</returns>
        Task<IEnumerable<RssSource>> GetActiveSourcesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// یک منبع RSS جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="rssSource">موجودیت منبع RSS که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(RssSource rssSource, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک منبع RSS موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند.
        /// </summary>
        /// <param name="rssSource">موجودیت منبع RSS با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateAsync(RssSource rssSource, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک منبع RSS را به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="rssSource">موجودیت منبع RSS که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(RssSource rssSource, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک منبع RSS را بر اساس شناسه آن به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="id">شناسه منبع RSS که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر حذف موفقیت‌آمیز بود، false اگر منبع پیدا نشد.</returns>
        Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// بررسی می‌کند که آیا منبعی با URL مشخص شده وجود دارد یا خیر (بدون در نظر گرفتن شناسه فعلی برای ویرایش).
        /// </summary>
        /// <param name="url">URL برای بررسی.</param>
        /// <param name="excludeId">شناسه‌ای که باید از بررسی منحصر به فرد بودن نادیده گرفته شود (برای حالت ویرایش).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر منبعی با این URL (به جز excludeId) وجود داشته باشد، در غیر این صورت false.</returns>
        Task<bool> ExistsByUrlAsync(string url, Guid? excludeId = null, CancellationToken cancellationToken = default);

        // متدهای احتمالی برای به‌روزرسانی فیلدهای اضافی:
        // Task UpdateLastFetchedAtAsync(Guid id, DateTime fetchedAt, CancellationToken cancellationToken = default);
        // Task IncrementFetchErrorCountAsync(Guid id, CancellationToken cancellationToken = default);
        // Task ResetFetchErrorCountAsync(Guid id, CancellationToken cancellationToken = default);
    }
}