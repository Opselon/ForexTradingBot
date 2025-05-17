using Domain.Entities; // برای SignalCategory

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت دسته‌بندی سیگنال (SignalCategory).
    /// عملیات مربوط به دسترسی و مدیریت داده‌های دسته‌بندی‌های سیگنال را تعریف می‌کند.
    /// </summary>
    public interface ISignalCategoryRepository
    {
        /// <summary>
        /// یک دسته‌بندی سیگنال را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه دسته‌بندی.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت دسته‌بندی سیگنال در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<SignalCategory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک دسته‌بندی سیگنال را بر اساس نام آن به صورت ناهمزمان پیدا می‌کند (نام باید منحصر به فرد باشد).
        /// </summary>
        /// <param name="name">نام دسته‌بندی.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت دسته‌بندی سیگنال در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<SignalCategory?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام دسته‌بندی‌های سیگنال را به صورت ناهمزمان برمی‌گرداند.
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تمام دسته‌بندی‌های سیگنال.</returns>
        Task<IEnumerable<SignalCategory>> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// یک دسته‌بندی سیگنال جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="category">موجودیت دسته‌بندی سیگنالی که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(SignalCategory category, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک دسته‌بندی سیگنال موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند.
        /// </summary>
        /// <param name="category">موجودیت دسته‌بندی سیگنالی با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateAsync(SignalCategory category, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک دسته‌بندی سیگنال را به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// باید با احتیاط استفاده شود، مخصوصاً اگر سیگنال‌هایی به این دسته مرتبط باشند.
        /// </summary>
        /// <param name="category">موجودیت دسته‌بندی سیگنالی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(SignalCategory category, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک دسته‌بندی سیگنال را بر اساس شناسه آن به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="id">شناسه دسته‌بندی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر حذف موفقیت‌آمیز بود، false اگر دسته‌بندی پیدا نشد.</returns>
        Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// بررسی می‌کند که آیا دسته‌بندی با نام مشخص شده وجود دارد یا خیر (بدون در نظر گرفتن شناسه فعلی برای ویرایش).
        /// </summary>
        /// <param name="name">نام برای بررسی.</param>
        /// <param name="excludeId">شناسه‌ای که باید از بررسی منحصر به فرد بودن نادیده گرفته شود (برای حالت ویرایش).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر دسته‌بندی با این نام (به جز excludeId) وجود داشته باشد، در غیر این صورت false.</returns>
        Task<bool> ExistsByNameAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);
    }
}