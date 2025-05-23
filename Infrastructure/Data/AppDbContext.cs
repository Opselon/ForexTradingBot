using Application.Common.Interfaces; // اطمینان از وجود این using
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection; // برای ApplyConfigurationsFromAssembly
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<AppDbContext> _logger;

        /// <summary>
        /// سازنده AppDbContext.
        /// </summary>
        /// <param name="options">گزینه‌های پیکربندی برای DbContext.</param>
        /// <param name="logger">لاگر برای لاگ کردن اطلاعات در زمان ذخیره تغییرات و پیکربندی مدل.</param>
        public AppDbContext(
            DbContextOptions<AppDbContext> options,
            ILogger<AppDbContext> logger) : base(options)
        {
            _logger = logger;
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
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await base.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Successfully saved {Count} changes to the database", result);
                return result;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "Concurrency conflict occurred while saving changes");
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error occurred while saving changes to the database");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while saving changes");
                throw;
            }
        }

        /// <summary>
        /// پیکربندی مدل داده‌ای با استفاده از Fluent API.
        /// این متد در زمان ایجاد مدل توسط EF Core فراخوانی می‌شود.
        /// </summary>
        /// <param name="modelBuilder">سازنده مدل برای پیکربندی موجودیت‌ها و روابط.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            try
            {
                base.OnModelCreating(modelBuilder);
                modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
                _logger.LogInformation("Successfully applied entity configurations");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while applying entity configurations");
                throw;
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                _logger.LogWarning("DbContext is not configured. Make sure to configure it in the service collection.");
            }
            base.OnConfiguring(optionsBuilder);
        }
    }
}