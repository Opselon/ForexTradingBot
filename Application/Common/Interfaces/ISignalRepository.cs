using Domain.Entities; // برای Signal, SignalCategory, SignalAnalysis
using System.Linq.Expressions;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت سیگنال (Signal).
    /// عملیات پایه CRUD و سایر متدهای خاص برای دسترسی به داده‌های سیگنال‌ها را تعریف می‌کند.
    /// </summary>
    public interface ISignalRepository
    {
        /// <summary>
        /// یک سیگنال را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند، همراه با دسته‌بندی و تحلیل‌های مرتبط.
        /// </summary>
        /// <param name="id">شناسه سیگنال.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت سیگنال در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<Signal?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک سیگنال را فقط با اطلاعات پایه‌ای آن بر اساس شناسه پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه سیگنال.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت سیگنال در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<Signal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام سیگنال‌ها را به صورت ناهمزمان برمی‌گرداند، همراه با دسته‌بندی آن‌ها.
        /// (برای تعداد زیاد سیگنال به صفحه‌بندی و فیلترینگ پیشرفته‌تر نیاز است).
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تمام سیگنال‌ها.</returns>
        Task<IEnumerable<Signal>> GetAllWithCategoryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// سیگنال‌هایی را که با یک شرط خاص مطابقت دارند به صورت ناهمزمان پیدا می‌کند، همراه با دسته‌بندی.
        /// </summary>
        /// <param name="predicate">شرط برای فیلتر کردن سیگنال‌ها.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از سیگنال‌های مطابق با شرط.</returns>
        Task<IEnumerable<Signal>> FindWithCategoryAsync(Expression<Func<Signal, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// سیگنال‌های اخیر را بر اساس تاریخ ایجاد و با محدودیت تعداد برمی‌گرداند.
        /// </summary>
        /// <param name="count">تعداد سیگنال‌های اخیر برای بازگرداندن.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از سیگنال‌های اخیر.</returns>
        Task<IEnumerable<Signal>> GetRecentSignalsAsync(int count, CancellationToken cancellationToken = default);

        /// <summary>
        /// سیگنال‌ها را بر اساس دسته‌بندی خاصی برمی‌گرداند.
        /// </summary>
        /// <param name="categoryId">شناسه دسته‌بندی.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از سیگنال‌های متعلق به دسته‌بندی مشخص شده.</returns>
        Task<IEnumerable<Signal>> GetSignalsByCategoryIdAsync(Guid categoryId, CancellationToken cancellationToken = default);

        /// <summary>
        /// سیگنال‌ها را بر اساس نماد معاملاتی (Symbol) برمی‌گرداند.
        /// </summary>
        /// <param name="symbol">نماد معاملاتی.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از سیگنال‌های متعلق به نماد مشخص شده.</returns>
        Task<IEnumerable<Signal>> GetSignalsBySymbolAsync(string symbol, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک سیگنال جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="signal">موجودیت سیگنالی که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(Signal signal, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک سیگنال موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند.
        /// </summary>
        /// <param name="signal">موجودیت سیگنالی با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateAsync(Signal signal, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک سیگنال را به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="signal">موجودیت سیگنالی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(Signal signal, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک سیگنال را بر اساس شناسه آن به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="id">شناسه سیگنالی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

        // متدهای بیشتر می‌توانند شامل صفحه‌بندی، مرتب‌سازی و فیلترهای پیچیده‌تر باشند.
        // Task<PagedList<Signal>> GetPagedSignalsAsync(SignalQueryParameters parameters, CancellationToken cancellationToken = default);
    }
}