using Application.Common.Interfaces; // اطمینان از وجود این using
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection; // برای ApplyConfigurationsFromAssembly

namespace Infrastructure.Data
{
    /// <summary>
    /// زمینه اصلی پایگاه داده برنامه (Database Context).
    /// این کلاس مسئول تعامل با پایگاه داده، تعریف DbSet ها برای هر موجودیت،
    /// و پیکربندی مدل داده‌ای با استفاده از Fluent API است.
    /// همچنین اینترفیس IAppDbContext را برای تسهیل تست و تزریق وابستگی پیاده‌سازی می‌کند.
    /// </summary>
    public class AppDbContext : DbContext, IAppDbContext
    {
        /// <summary>
        /// سازنده AppDbContext.
        /// </summary>
        /// <param name="options">گزینه‌های پیکربندی برای DbContext.</param>
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        // DbSet ها برای هر موجودیت در دامنه
        public DbSet<User> Users => Set<User>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<Transaction> Transactions => Set<Transaction>();
        public DbSet<Signal> Signals => Set<Signal>();
        public DbSet<SignalCategory> SignalCategories => Set<SignalCategory>();
        public DbSet<UserSignalPreference> UserSignalPreferences => Set<UserSignalPreference>();
        public DbSet<RssSource> RssSources => Set<RssSource>();
        public DbSet<SignalAnalysis> SignalAnalyses => Set<SignalAnalysis>();
        public DbSet<TokenWallet> TokenWallets => Set<TokenWallet>();
        public DbSet<NewsItem> NewsItems => Set<NewsItem>();
        public DbSet<Domain.Features.Forwarding.Entities.ForwardingRule> ForwardingRules => Set<Domain.Features.Forwarding.Entities.ForwardingRule>();

        /// <summary>
        /// ذخیره تغییرات انجام شده در DbContext به صورت ناهمزمان.
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>تعداد رکوردهای تغییر یافته در پایگاه داده.</returns>
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // می‌توان منطق اضافی مانند به‌روزرسانی خودکار فیلدهای Auditable (CreatedAt, UpdatedAt) را در اینجا اضافه کرد.
            // به عنوان مثال، برای موجودیت‌هایی که از یک اینترفیس IAuditableEntity ارث‌بری می‌کنند.
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
        /// پیکربندی مدل داده‌ای با استفاده از Fluent API.
        /// این متد در زمان ایجاد مدل توسط EF Core فراخوانی می‌شود.
        /// </summary>
        /// <param name="modelBuilder">سازنده مدل برای پیکربندی موجودیت‌ها و روابط.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // اعمال تمام پیکربندی‌های تعریف شده در کلاس‌هایی که IEntityTypeConfiguration<TEntity> را پیاده‌سازی می‌کنند
            // از اسمبلی جاری. این روش برای سازماندهی بهتر پیکربندی‌ها توصیه می‌شود.
            // modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
            // اگر از روش بالا استفاده نمی‌کنید، پیکربندی‌ها را مستقیماً در اینجا تعریف کنید:
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
            // --- User Configuration ---
        }
    }
}