using Domain.Entities;
using Microsoft.EntityFrameworkCore; // برای DbSet

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیسی برای AppDbContext که توسط لایه Application برای تعامل با پایگاه داده استفاده می‌شود.
    /// این اینترفیس به جداسازی لایه Application از پیاده‌سازی مشخص لایه Infrastructure (EF Core) کمک می‌کند
    /// و امکان تست واحد (Unit Testing) سرویس‌های Application را فراهم می‌آورد.
    /// </summary>
    public interface IAppDbContext
    {
        /// <summary>
        /// مجموعه داده‌ای برای موجودیت کاربران.
        /// </summary>
        DbSet<User> Users { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت کیف پول‌های توکن.
        /// </summary>
        DbSet<TokenWallet> TokenWallets { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت اشتراک‌ها.
        /// </summary>
        DbSet<Subscription> Subscriptions { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت تراکنش‌ها.
        /// </summary>
        DbSet<Transaction> Transactions { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت تنظیمات برگزیده سیگنال کاربران.
        /// </summary>
        DbSet<UserSignalPreference> UserSignalPreferences { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت سیگنال‌ها.
        /// </summary>
        DbSet<Signal> Signals { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت دسته‌بندی‌های سیگنال.
        /// </summary>
        DbSet<SignalCategory> SignalCategories { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت منابع RSS.
        /// </summary>
        DbSet<RssSource> RssSources { get; }

        /// <summary>
        /// مجموعه داده‌ای برای موجودیت تحلیل‌های سیگنال.
        /// </summary>
        DbSet<SignalAnalysis> SignalAnalyses { get; }


        // سایر متدها و خصوصیات DbContext که ممکن است لایه Application به آن‌ها نیاز داشته باشد:
        // Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }
        // Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }
        // EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

        /// <summary>
        /// تغییرات انجام شده در context را به صورت ناهمزمان در پایگاه داده ذخیره می‌کند.
        /// </summary>
        /// <param name="cancellationToken">یک <see cref="CancellationToken"/> برای مشاهده درخواست‌های لغو عملیات.</param>
        /// <returns>
        /// یک <see cref="Task"/> که نتیجه ناهمزمان عملیات ذخیره‌سازی را نشان می‌دهد.
        /// نتیجه وظیفه، تعداد اشیاء نوشته شده در پایگاه داده را شامل می‌شود.
        /// </returns>
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}