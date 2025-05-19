using Domain.Entities; // برای دسترسی به User
using System.Linq.Expressions; // برای Expression<Func<User, bool>>

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت کاربر (User).
    /// عملیات پایه CRUD و سایر متدهای خاص برای دسترسی به داده‌های کاربران را تعریف می‌کند.
    /// </summary>
    public interface IUserRepository
    {
        /// <summary>
        /// یک کاربر را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت کاربر در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کاربر را بر اساس شناسه تلگرام آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="telegramId">شناسه تلگرام کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت کاربر در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<User?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کاربر را بر اساس آدرس ایمیل آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="email">آدرس ایمیل کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت کاربر در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام کاربران را به صورت ناهمزمان برمی‌گرداند.
        /// (احتیاط: برای تعداد زیاد کاربران ممکن است به صفحه‌بندی نیاز باشد).
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تمام کاربران.</returns>
        Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// کاربرانی را که با یک شرط خاص مطابقت دارند به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="predicate">شرط برای فیلتر کردن کاربران.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از کاربران مطابق با شرط.</returns>
        Task<IEnumerable<User>> FindAsync(Expression<Func<User, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کاربر جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="user">موجودیت کاربری که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>Task.</returns>
        Task AddAsync(User user, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک کاربر موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند.
        /// </summary>
        /// <param name="user">موجودیت کاربری با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>Task.</returns>
        Task UpdateAsync(User user, CancellationToken cancellationToken = default); // EF Core به طور خودکار تغییرات را ردیابی می‌کند، این متد می‌تواند خالی باشد یا فقط SaveChangesAsync را فراخوانی کند.

        /// <summary>
        /// یک کاربر را به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="user">موجودیت کاربری که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>Task.</returns>
        Task DeleteAsync(User user, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کاربر را بر اساس شناسه آن به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="id">شناسه کاربری که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>Task.</returns>
        Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// بررسی می‌کند که آیا کاربری با ایمیل مشخص شده وجود دارد یا خیر.
        /// </summary>
        /// <param name="email">ایمیل برای بررسی.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر کاربر وجود داشته باشد، در غیر این صورت false.</returns>
        Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default);

        /// <summary>
        /// بررسی می‌کند که آیا کاربری با شناسه تلگرام مشخص شده وجود دارد یا خیر.
        /// </summary>
        /// <param name="telegramId">شناسه تلگرام برای بررسی.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر کاربر وجود داشته باشد، در غیر این صورت false.</returns>
        Task<bool> ExistsByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default);


        Task<IEnumerable<User>> GetUsersWithNotificationSettingAsync(
            Expression<Func<User, bool>> notificationPredicate,
            CancellationToken cancellationToken = default);


        Task<IEnumerable<User>> GetUsersForNewsNotificationAsync(
            Guid? newsItemSignalCategoryId,
            bool isNewsItemVipOnly,
            CancellationToken cancellationToken = default);
        // متدهای دیگری که ممکن است نیاز داشته باشید:
        // Task<User?> GetUserWithSubscriptionsAsync(Guid userId, CancellationToken cancellationToken = default);
        // Task<User?> GetUserWithTokenWalletAsync(Guid userId, CancellationToken cancellationToken = default);
        // Task<int> CountAsync(CancellationToken cancellationToken = default);
    }
}