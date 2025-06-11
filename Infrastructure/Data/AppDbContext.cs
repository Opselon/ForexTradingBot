// File: Infrastructure\Data\AppDbContext.cs // مسیر فایل را اصلاح کردیم تا با Namespace و محتوا مطابقت داشته باشد
using Application.Common.Interfaces;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection; // برای ApplyConfigurationsFromAssembly

namespace Infrastructure.Data
{
    /// <summary>
    /// زمینه اصلی پایگاه داده برنامه (Database Context) برای Entity Framework Core.
    /// این کلاس مسئول مدیریت جلسات پایگاه داده، تعریف DbSet ها برای هر موجودیت دامنه،
    /// و پیکربندی مدل داده‌ای با استفاده از Fluent API است.
    /// همچنین اینترفیس <see cref="IAppDbContext"/> را برای تسهیل تست و تزریق وابستگی پیاده‌سازی می‌کند.
    /// </summary>
    public class AppDbContext : DbContext, IAppDbContext
    {
        /// <summary>
        /// سازنده <see cref="AppDbContext"/>.
        /// گزینه‌های پیکربندی DbContext مانند رشته اتصال و فراهم‌کننده پایگاه داده را دریافت می‌کند.
        /// </summary>
        /// <param name="options">گزینه‌های پیکربندی برای DbContext.</param>
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// DbSet برای موجودیت‌های User.
        /// </summary>
        public DbSet<User> Users => Set<User>();

        /// <summary>
        /// DbSet برای موجودیت‌های Subscription.
        /// </summary>
        public DbSet<Subscription> Subscriptions => Set<Subscription>();

        /// <summary>
        /// DbSet برای موجودیت‌های Transaction.
        /// </summary>
        public DbSet<Transaction> Transactions => Set<Transaction>();

        /// <summary>
        /// DbSet برای موجودیت‌های Signal.
        /// </summary>
        public DbSet<Signal> Signals => Set<Signal>();

        /// <summary>
        /// DbSet برای موجودیت‌های SignalCategory.
        /// </summary>
        public DbSet<SignalCategory> SignalCategories => Set<SignalCategory>();

        /// <summary>
        /// DbSet برای موجودیت‌های UserSignalPreference.
        /// </summary>
        public DbSet<UserSignalPreference> UserSignalPreferences => Set<UserSignalPreference>();

        /// <summary>
        /// DbSet برای موجودیت‌های RssSource.
        /// </summary>
        public DbSet<RssSource> RssSources => Set<RssSource>();

        /// <summary>
        /// DbSet برای موجودیت‌های SignalAnalysis.
        /// </summary>
        public DbSet<SignalAnalysis> SignalAnalyses => Set<SignalAnalysis>();

        /// <summary>
        /// DbSet برای موجودیت‌های TokenWallet.
        /// </summary>
        public DbSet<TokenWallet> TokenWallets => Set<TokenWallet>();

        /// <summary>
        /// DbSet برای موجودیت‌های NewsItem.
        /// </summary>
        public DbSet<NewsItem> NewsItems => Set<NewsItem>();

        /// <summary>
        /// DbSet برای موجودیت‌های ForwardingRule (از فضای نام خاص).
        /// </summary>
        public DbSet<Domain.Features.Forwarding.Entities.ForwardingRule> ForwardingRules => Set<Domain.Features.Forwarding.Entities.ForwardingRule>();

        /// <summary>
        /// ذخیره تغییرات انجام شده در DbContext به صورت ناهمزمان در پایگاه داده.
        /// این متد را می‌توان برای افزودن منطق مشترک قبل از ذخیره، مانند به‌روزرسانی فیلدهای Auditable، Override کرد.
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات (اختیاری).</param>
        /// <returns>تعداد رکوردهای تغییر یافته (درج، به‌روزرسانی یا حذف شده) در پایگاه داده.</returns>
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // مثال: افزودن منطق برای به‌روزرسانی خودکار فیلدهای Auditable (CreatedAt, UpdatedAt)
            // این بخش در صورت فعال‌سازی، فیلدهای زمانی موجودیت‌هایی را که از یک اینترفیس IAuditableEntity ارث‌بری می‌کنند، به‌روزرسانی می‌کند.
            // foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
            // {
            //     switch (entry.State)
            //     {
            //         case EntityState.Added:
            //             entry.Entity.CreatedAt = DateTime.UtcNow;
            //             break;
            //         case EntityState.Modified:
            //             entry.Entity.UpdatedAt = DateTime.UtcNow;
            //             break;
            //     }
            // }
            return base.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// پیکربندی مدل داده‌ای برای Entity Framework Core با استفاده از Fluent API.
        /// این متد در زمان ایجاد مدل توسط EF Core فراخوانی می‌شود و برای تعریف نگاشت‌ها (mappings)،
        /// روابط و محدودیت‌های پایگاه داده استفاده می‌گردد.
        /// </summary>
        /// <param name="modelBuilder">سازنده مدل برای پیکربندی موجودیت‌ها و روابط.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // اعمال تمام پیکربندی‌های موجودیت (entity configurations) که کلاس IEntityTypeConfiguration<TEntity>
            // را پیاده‌سازی می‌کنند و در اسمبلی جاری (assembly containing this DbContext) قرار دارند.
            // این روش برای سازماندهی بهتر و جداسازی منطق پیکربندی توصیه می‌شود.
            _ = modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        }
    }
}