// File: Infrastructure/Features/Forwarding/Repositories/ForwardingRuleRepository.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Domain.Features.Forwarding.Entities;     // For ForwardingRule entity
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository interface
using Infrastructure.Data;                     // For AppDbContext (assuming this is the correct namespace)
using Microsoft.EntityFrameworkCore; // For EF Core functionalities
using Microsoft.Extensions.Logging;   // For logging
using Polly; // برای استفاده از Polly
using Polly.Retry; // برای سیاست‌های Retry
using System.Data.Common; // برای DbException (کلاس پایه برای استثناهای دیتابیس)
#endregion

namespace Infrastructure.Features.Forwarding.Repositories
{
    /// <summary>
    /// پیاده‌سازی ریپازیتوری برای مدیریت قوانین فورواردینگ (ForwardingRule).
    /// این کلاس عملیات CRUD را برای موجودیت ForwardingRule فراهم می‌کند و از Polly برای افزایش پایداری در برابر خطاهای گذرا پایگاه داده استفاده می‌کند.
    /// </summary>
    public class ForwardingRuleRepository : IForwardingRuleRepository
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<ForwardingRuleRepository> _logger;
        private readonly AsyncRetryPolicy _retryPolicy; // فیلد جدید برای سیاست Polly

        /// <summary>
        /// نمونه جدیدی از کلاس ForwardingRuleRepository را ایجاد می‌کند.
        /// </summary>
        /// <param name="dbContext">زمینه پایگاه داده برنامه.</param>
        /// <param name="logger">لاگر برای ثبت اطلاعات و خطاها.</param>
        public ForwardingRuleRepository(
            AppDbContext dbContext,
            ILogger<ForwardingRuleRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // مقداردهی اولیه سیاست Polly برای تلاش مجدد در برابر خطاهای گذرا پایگاه داده.
            // این سیاست هر گونه DbException (مانند SqlException، NpgsqlException و غیره) را مدیریت می‌کند،
            // اما به طور صریح DbUpdateConcurrencyException را نادیده می‌گیرد زیرا نیازمند حل تعارض است نه تلاش مجدد.
            _retryPolicy = Policy
                .Handle<DbException>(ex => !(ex is DbUpdateConcurrencyException)) // خطاهای پایگاه داده را مدیریت می‌کند، به جز خطاهای همزمانی
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "ForwardingRuleRepository: Transient database error encountered. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }

        #region Read Operations

        /// <summary>
        /// یک قانون فورواردینگ را بر اساس نام آن به صورت ناهمزمان بازیابی می‌کند.
        /// این عملیات با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="ruleName">نام قانون فورواردینگ.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        /// <returns>قانون فورواردینگ یافت شده یا null اگر یافت نشود.</returns>
        public async Task<ForwardingRule?> GetByIdAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: GetByIdAsync called with null or empty ruleName.");
                return null;
            }
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rule by RuleName: {RuleName}", ruleName);

            return await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد
            {
                return await _dbContext.ForwardingRules
                    .FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken);
            });
        }

        /// <summary>
        /// تمام قوانین فورواردینگ را به صورت ناهمزمان بازیابی می‌کند.
        /// این عملیات با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        /// <returns>لیستی از تمام قوانین فورواردینگ.</returns>
        public async Task<IEnumerable<ForwardingRule>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching all forwarding rules, AsNoTracking.");

            return await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد
            {
                return await _dbContext.ForwardingRules
                    .AsNoTracking()
                    .OrderBy(r => r.RuleName)
                    .ToListAsync(cancellationToken);
            });
        }

        /// <summary>
        /// قوانین فورواردینگ را بر اساس شناسه کانال منبع به صورت ناهمزمان بازیابی می‌کند.
        /// این عملیات با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="sourceChannelId">شناسه کانال منبع.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        /// <returns>لیستی از قوانین فورواردینگ مرتبط با کانال منبع.</returns>
        public async Task<IEnumerable<ForwardingRule>> GetBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rules by SourceChannelId: {SourceChannelId}, AsNoTracking.", sourceChannelId);

            return await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد
            {
                return await _dbContext.ForwardingRules
                    .Where(r => r.SourceChannelId == sourceChannelId)
                    .AsNoTracking()
                    .OrderBy(r => r.RuleName)
                    .ToListAsync(cancellationToken);
            });
        }

        /// <summary>
        /// قوانین فورواردینگ را به صورت صفحه بندی شده به صورت ناهمزمان بازیابی می‌کند.
        /// این عملیات با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="pageNumber">شماره صفحه (شروع از 1).</param>
        /// <param name="pageSize">تعداد آیتم‌ها در هر صفحه.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        /// <returns>لیستی از قوانین فورواردینگ برای صفحه مشخص شده.</returns>
        public async Task<IEnumerable<ForwardingRule>> GetPaginatedAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            if (pageNumber <= 0) pageNumber = 1;
            if (pageSize <= 0) pageSize = 10; // Default page size

            _logger.LogTrace("ForwardingRuleRepository: Fetching paginated forwarding rules - Page: {PageNumber}, Size: {PageSize}, AsNoTracking.", pageNumber, pageSize);

            return await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد
            {
                return await _dbContext.ForwardingRules
                    .AsNoTracking()
                    .OrderBy(r => r.RuleName)
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);
            });
        }

        /// <summary>
        /// تعداد کل قوانین فورواردینگ را به صورت ناهمزمان دریافت می‌کند.
        /// این عملیات با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        /// <returns>تعداد کل قوانین فورواردینگ.</returns>
        public async Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Getting total count of forwarding rules.");

            return await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد
            {
                return await _dbContext.ForwardingRules
                    .AsNoTracking()
                    .CountAsync(cancellationToken);
            });
        }

        #endregion

        #region Write Operations (with SaveChangesAsync inside Repository)

        /// <summary>
        /// یک قانون فورواردینگ جدید را به صورت ناهمزمان اضافه می‌کند.
        /// عملیات ذخیره با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="rule">قانون فورواردینگ برای اضافه کردن.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        public async Task AddAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a ForwardingRule with null or empty RuleName.");
                throw new ArgumentException("RuleName cannot be null or empty.", nameof(rule.RuleName));
            }
            _logger.LogInformation("ForwardingRuleRepository: Adding forwarding rule with RuleName: {RuleName}, SourceChannelId: {SourceChannelId}",
                                   rule.RuleName, rule.SourceChannelId);
            try
            {
                await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد برای عملیات نوشتن
                {
                    await _dbContext.ForwardingRules.AddAsync(rule, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                });
                _logger.LogInformation("ForwardingRuleRepository: Successfully added forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateException dbEx) // اگر Polly تمام تلاش‌ها را انجام دهد و باز هم خطا رخ دهد
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error adding forwarding rule {RuleName} to the database after retries.", rule.RuleName);
                throw;
            }
            catch (Exception ex) // گرفتن هر گونه خطای غیرمنتظره دیگر
            {
                _logger.LogError(ex, "ForwardingRuleRepository: An unexpected error occurred while adding forwarding rule {RuleName}.", rule.RuleName);
                throw;
            }
        }

        /// <summary>
        /// یک قانون فورواردینگ موجود را به صورت ناهمزمان به‌روزرسانی می‌کند.
        /// عملیات ذخیره با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="rule">قانون فورواردینگ برای به‌روزرسانی.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        public async Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update with a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update a ForwardingRule without a valid RuleName (used as identifier).");
                throw new ArgumentException("RuleName cannot be null or empty for update identification.", nameof(rule.RuleName));
            }
            _logger.LogInformation("ForwardingRuleRepository: Updating forwarding rule with RuleName: {RuleName}", rule.RuleName);
            try
            {
                await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد برای عملیات نوشتن
                {
                    _dbContext.ForwardingRules.Update(rule);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                });
                _logger.LogInformation("ForwardingRuleRepository: Successfully updated forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateConcurrencyException concEx) // خطای همزمانی که Polly آن را مدیریت نمی‌کند
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while updating forwarding rule {RuleName}. The rule may have been modified or deleted by another user.", rule.RuleName);
                throw;
            }
            catch (DbUpdateException dbEx) // اگر Polly تمام تلاش‌ها را انجام دهد و باز هم خطا رخ دهد
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error updating forwarding rule {RuleName} in the database after retries.", rule.RuleName);
                throw;
            }
            catch (Exception ex) // گرفتن هر گونه خطای غیرمنتظره دیگر
            {
                _logger.LogError(ex, "ForwardingRuleRepository: An unexpected error occurred while updating forwarding rule {RuleName}.", rule.RuleName);
                throw;
            }
        }

        /// <summary>
        /// یک قانون فورواردینگ را بر اساس نام آن به صورت ناهمزمان حذف می‌کند.
        /// عملیات حذف و ذخیره با سیاست تلاش مجدد Polly محافظت می‌شوند.
        /// </summary>
        /// <param name="ruleName">نام قانون فورواردینگ برای حذف.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات به صورت ناهمزمان.</param>
        public async Task DeleteAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: DeleteAsync called with null or empty ruleName.");
                return;
            }
            _logger.LogInformation("ForwardingRuleRepository: Attempting to delete forwarding rule with RuleName: {RuleName}", ruleName);

            // اولین جستجو برای یافتن آیتم نیز می‌تواند با Polly محافظت شود
            var ruleToDelete = await _retryPolicy.ExecuteAsync(async () =>
            {
                return await _dbContext.ForwardingRules.FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken);
            });

            if (ruleToDelete == null)
            {
                _logger.LogWarning("ForwardingRuleRepository: Forwarding rule with RuleName {RuleName} not found for deletion.", ruleName);
                return;
            }

            try
            {
                await _retryPolicy.ExecuteAsync(async () => // اعمال سیاست تلاش مجدد برای عملیات حذف و ذخیره
                {
                    _dbContext.ForwardingRules.Remove(ruleToDelete);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                });
                _logger.LogInformation("ForwardingRuleRepository: Successfully deleted forwarding rule: {RuleName}", ruleName);
            }
            catch (DbUpdateConcurrencyException concEx) // خطای همزمانی که Polly آن را مدیریت نمی‌کند
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while deleting forwarding rule {RuleName}.", ruleName);
                throw;
            }
            catch (DbUpdateException dbEx) // اگر Polly تمام تلاش‌ها را انجام دهد و باز هم خطا رخ دهد
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error deleting forwarding rule {RuleName} from the database after retries. It might be in use.", ruleName);
                throw;
            }
            catch (Exception ex) // گرفتن هر گونه خطای غیرمنتظره دیگر
            {
                _logger.LogError(ex, "ForwardingRuleRepository: An unexpected error occurred while deleting forwarding rule {RuleName}.", ruleName);
                throw;
            }
        }
    }
    #endregion
}