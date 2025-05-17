using Domain.Entities; // برای SignalAnalysis و Signal
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت تحلیل سیگنال (SignalAnalysis).
    /// عملیات مربوط به دسترسی و مدیریت داده‌های تحلیل‌های انجام شده بر روی سیگنال‌ها را تعریف می‌کند.
    /// </summary>
    public interface ISignalAnalysisRepository
    {
        /// <summary>
        /// یک تحلیل سیگنال را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه تحلیل سیگنال.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت تحلیل سیگنال در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<SignalAnalysis?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام تحلیل‌های مربوط به یک سیگنال خاص را به صورت ناهمزمان برمی‌گرداند.
        /// </summary>
        /// <param name="signalId">شناسه سیگنالی که تحلیل‌ها برای آن جستجو می‌شوند.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از تحلیل‌های مربوط به سیگنال مشخص شده.</returns>
        Task<IEnumerable<SignalAnalysis>> GetAnalysesBySignalIdAsync(Guid signalId, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک تحلیل سیگنال جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// </summary>
        /// <param name="analysis">موجودیت تحلیل سیگنالی که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(SignalAnalysis analysis, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک تحلیل سیگنال موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند (استفاده با احتیاط).
        /// </summary>
        /// <param name="analysis">موجودیت تحلیل سیگنالی با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateAsync(SignalAnalysis analysis, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک تحلیل سیگنال را به صورت ناهمزمان از پایگاه داده حذف می‌کند (استفاده با احتیاط).
        /// </summary>
        /// <param name="analysis">موجودیت تحلیل سیگنالی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteAsync(SignalAnalysis analysis, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک تحلیل سیگنال را بر اساس شناسه آن به صورت ناهمزمان از پایگاه داده حذف می‌کند.
        /// </summary>
        /// <param name="id">شناسه تحلیل سیگنالی که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر حذف موفقیت‌آمیز بود، false اگر تحلیل پیدا نشد.</returns>
        Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    }
}