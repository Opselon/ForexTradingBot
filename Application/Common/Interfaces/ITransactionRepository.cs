using Domain.Entities; // برای Transaction و User
using Domain.Enums;   // برای TransactionType
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت تراکنش (Transaction).
    /// عملیات مربوط به دسترسی و مدیریت داده‌های تراکنش‌های مالی یا توکنی کاربران را تعریف می‌کند.
    /// </summary>
    public interface ITransactionRepository
    {
        /// <summary>
        /// یک تراکنش را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه تراکنش.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت تراکنش در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام تراکنش‌های یک کاربر خاص را به صورت ناهمزمان برمی‌گرداند.
        /// </summary>
        /// <param name="userId">شناسه کاربری که تراکنش‌ها برای او جستجو می‌شوند.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تراکنش‌های کاربر.</returns>
        Task<IEnumerable<Transaction>> GetTransactionsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// تراکنش‌های یک کاربر خاص را بر اساس نوع و/یا بازه زمانی فیلتر و برمی‌گرداند.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="type">نوع تراکنش (اختیاری).</param>
        /// <param name="startDate">تاریخ شروع بازه (اختیاری).</param>
        /// <param name="endDate">تاریخ پایان بازه (اختیاری).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تراکنش‌های فیلتر شده کاربر.</returns>
        Task<IEnumerable<Transaction>> GetFilteredTransactionsByUserIdAsync(
            Guid userId,
            TransactionType? type = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// یک تراکنش جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="transaction">موجودیت تراکنشی که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);

        // معمولاً تراکنش‌ها پس از ایجاد، ویرایش یا حذف نمی‌شوند.
        // اگر نیاز به اصلاح باشد، معمولاً یک تراکنش جبرانی (compensating transaction) ایجاد می‌شود.
        // Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default);
        // Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default);
    }
}