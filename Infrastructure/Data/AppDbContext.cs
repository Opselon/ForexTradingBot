using Application.Common.Interfaces; // اطمینان از وجود این using
using Domain.Entities;
using Domain.Enums; // برای استفاده از enum ها در property conversion یا seeding
using Domain.ValueObjects; // برای TokenAmount
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion; // برای EnumToStringConverter
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

            // --- User Configuration ---
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("Users"); // نام جدول (اختیاری، EF Core به طور پیش‌فرض از نام DbSet استفاده می‌کند)
                entity.HasKey(u => u.Id);

                entity.Property(u => u.Username)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.HasIndex(u => u.Username).IsUnique(); // نام کاربری باید منحصر به فرد باشد

                entity.Property(u => u.TelegramId)
                    .IsRequired()
                    .HasMaxLength(50); // معمولاً شناسه تلگرام عددی است اما به عنوان رشته ذخیره می‌شود
                entity.HasIndex(u => u.TelegramId).IsUnique(); // شناسه تلگرام باید منحصر به فرد باشد

                entity.Property(u => u.Email)
                    .IsRequired()
                    .HasMaxLength(200);
                entity.HasIndex(u => u.Email).IsUnique(); // ایمیل باید منحصر به فرد باشد

                entity.Property(u => u.Level)
                    .IsRequired()
                    .HasConversion(new EnumToStringConverter<UserLevel>()); // ذخیره enum به عنوان رشته برای خوانایی بهتر در دیتابیس

                entity.Property(u => u.CreatedAt).IsRequired();

                // Relation: User 1-* Subscriptions
                entity.HasMany(u => u.Subscriptions)
                    .WithOne(s => s.User)
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade); // اگر کاربر حذف شد، اشتراک‌هایش هم حذف شوند

                // Relation: User 1-* Transactions
                entity.HasMany(u => u.Transactions)
                    .WithOne(t => t.User)
                    .HasForeignKey(t => t.UserId)
                    .OnDelete(DeleteBehavior.Cascade); // اگر کاربر حذف شد، تراکنش‌هایش هم حذف شوند

                // Relation: User 1-* UserSignalPreferences
                entity.HasMany(u => u.Preferences)
                    .WithOne(p => p.User)
                    .HasForeignKey(p => p.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Relation: User 1-1 TokenWallet
                entity.HasOne(u => u.TokenWallet)
                    .WithOne(tw => tw.User)
                    .HasForeignKey<TokenWallet>(tw => tw.UserId)
                    .OnDelete(DeleteBehavior.Cascade); // اگر کاربر حذف شد، کیف پولش هم حذف شود
            });

            // --- TokenWallet Configuration ---
            modelBuilder.Entity<TokenWallet>(entity =>
            {
                entity.ToTable("TokenWallets");
                entity.HasKey(tw => tw.Id);

                entity.Property(tw => tw.UserId).IsRequired(); // کلید خارجی به User

                entity.Property(tw => tw.Balance)
                    .IsRequired()
                    .HasColumnType("decimal(18,4)"); // دقت مشخص برای موجودی توکن (مطابق با TokenAmount.Value)

                entity.Property(tw => tw.UpdatedAt).IsRequired();

                // Value Object TokenAmount (اگر به عنوان Owned Entity پیکربندی می‌شد)
                // entity.OwnsOne(tw => tw.Balance, balanceBuilder =>
                // {
                //    balanceBuilder.Property(a => a.Value)
                //        .HasColumnName("Balance") // نام ستون در جدول TokenWallets
                //        .HasColumnType("decimal(18,4)")
                //        .IsRequired();
                // });
                // اما چون TokenAmount.Value مستقیماً به TokenWallet.Balance مپ شده، این لازم نیست.
            });

            // --- Subscription Configuration ---
            modelBuilder.Entity<Subscription>(entity =>
            {
                entity.ToTable("Subscriptions");
                entity.HasKey(s => s.Id);

                entity.Property(s => s.UserId).IsRequired();
                entity.Property(s => s.StartDate).IsRequired();
                entity.Property(s => s.EndDate).IsRequired();
                entity.Property(s => s.CreatedAt).IsRequired();

                // IsActive یک calculated property است و در دیتابیس ذخیره نمی‌شود.
                entity.Ignore(s => s.IsActive);

                // اگر Subscription به یک SubscriptionPlan مرتبط بود:
                // entity.Property(s => s.SubscriptionPlanId).IsRequired();
                // entity.HasOne(s => s.Plan).WithMany().HasForeignKey(s => s.SubscriptionPlanId);
            });

            // --- Transaction Configuration ---
            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.ToTable("Transactions");
                entity.HasKey(t => t.Id);

                entity.Property(t => t.UserId).IsRequired();
                entity.Property(t => t.Amount)
                    .IsRequired()
                    .HasColumnType("decimal(18,4)"); // دقت برای مبلغ تراکنش

                entity.Property(t => t.Type)
                    .IsRequired()
                    .HasConversion(new EnumToStringConverter<TransactionType>());

                entity.Property(t => t.Description).HasMaxLength(500); // اختیاری
                entity.Property(t => t.Timestamp).IsRequired();
            });

            // --- SignalCategory Configuration ---
            modelBuilder.Entity<SignalCategory>(entity =>
            {
                entity.ToTable("SignalCategories");
                entity.HasKey(sc => sc.Id);

                entity.Property(sc => sc.Name)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.HasIndex(sc => sc.Name).IsUnique(); // نام دسته باید منحصر به فرد باشد

                // Relation: SignalCategory 1-* Signals
                entity.HasMany(sc => sc.Signals)
                    .WithOne(s => s.Category)
                    .HasForeignKey(s => s.CategoryId)
                    .OnDelete(DeleteBehavior.Restrict); // اگر دسته‌ای سیگنال فعال دارد، حذف نشود (یا SetNull اگر CategoryId در Signal می‌تواند null باشد)
            });

            // --- Signal Configuration ---
            modelBuilder.Entity<Signal>(entity =>
            {
                entity.ToTable("Signals");
                entity.HasKey(s => s.Id);

                entity.Property(s => s.Type)
                    .IsRequired()
                    .HasConversion(new EnumToStringConverter<SignalType>());

                entity.Property(s => s.Symbol)
                    .IsRequired()
                    .HasMaxLength(50);
                entity.HasIndex(s => s.Symbol); // ایندکس برای جستجوی سریع‌تر بر اساس نماد

                entity.Property(s => s.EntryPrice).IsRequired().HasColumnType("decimal(18,8)"); // دقت بیشتر برای قیمت‌ها
                entity.Property(s => s.StopLoss).IsRequired().HasColumnType("decimal(18,8)");
                entity.Property(s => s.TakeProfit).IsRequired().HasColumnType("decimal(18,8)");

                entity.Property(s => s.Source)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(s => s.CategoryId).IsRequired();
                entity.Property(s => s.CreatedAt).IsRequired();

                // Relation: Signal 1-* SignalAnalyses
                entity.HasMany(s => s.Analyses)
                    .WithOne(sa => sa.Signal)
                    .HasForeignKey(sa => sa.SignalId)
                    .OnDelete(DeleteBehavior.Cascade); // اگر سیگنال حذف شد، تحلیل‌هایش هم حذف شوند
            });

            // --- SignalAnalysis Configuration ---
            modelBuilder.Entity<SignalAnalysis>(entity =>
            {
                entity.ToTable("SignalAnalyses");
                entity.HasKey(sa => sa.Id);

                entity.Property(sa => sa.SignalId).IsRequired();

                entity.Property(sa => sa.AnalystName)
                    .IsRequired()
                    .HasMaxLength(150);

                entity.Property(sa => sa.Notes)
                    .IsRequired()
                    .HasMaxLength(2000);

                entity.Property(sa => sa.CreatedAt).IsRequired();
            });

            // --- UserSignalPreference Configuration (Join Table) ---
            modelBuilder.Entity<UserSignalPreference>(entity =>
            {
                entity.ToTable("UserSignalPreferences");
                entity.HasKey(usp => usp.Id); // یا کلید ترکیبی (UserId, CategoryId)

                // entity.HasKey(usp => new { usp.UserId, usp.CategoryId }); // اگر Id جداگانه نمی‌خواهید

                entity.Property(usp => usp.UserId).IsRequired();
                entity.Property(usp => usp.CategoryId).IsRequired();
                entity.Property(usp => usp.CreatedAt).IsRequired();

                // Relation: UserSignalPreference *-1 User (handled by User entity's HasMany)
                // Relation: UserSignalPreference *-1 SignalCategory
                entity.HasOne(usp => usp.Category)
                    .WithMany() // یک دسته می‌تواند در تنظیمات چندین کاربر باشد (بدون پراپرتی نویگیشن در SignalCategory)
                    .HasForeignKey(usp => usp.CategoryId)
                    .OnDelete(DeleteBehavior.Cascade); // اگر دسته حذف شد، تنظیمات مرتبط با آن هم حذف شوند
            });

            // --- RssSource Configuration ---
            modelBuilder.Entity<RssSource>(entity =>
            {
                entity.ToTable("RssSources");
                entity.HasKey(rs => rs.Id);

                entity.Property(rs => rs.Url)
                    .IsRequired()
                    .HasMaxLength(500);
                entity.HasIndex(rs => rs.Url).IsUnique(); // URL باید منحصر به فرد باشد

                entity.Property(rs => rs.SourceName)
                    .IsRequired()
                    .HasMaxLength(150);

                entity.Property(rs => rs.IsActive).IsRequired();
                entity.Property(rs => rs.CreatedAt).IsRequired();

                // فیلدهای پیشنهادی مانند LastFetchedAt و غیره نیز در اینجا پیکربندی می‌شوند
                // entity.Property(rs => rs.LastFetchedAt).IsOptional();
            });
            // در Infrastructure/Data/AppDbContext.cs
            // داخل متد OnModelCreating:

            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.ToTable("Transactions");
                entity.HasKey(t => t.Id);

                entity.Property(t => t.UserId).IsRequired();
                entity.Property(t => t.Amount)
                    .IsRequired()
                    .HasColumnType("decimal(18,4)"); // یا هر دقت دیگری که برای مبلغ لازم دارید

                entity.Property(t => t.Type)
                    .IsRequired()
                    .HasConversion(new EnumToStringConverter<TransactionType>()); // ذخیره enum به عنوان رشته

                entity.Property(t => t.Description).HasMaxLength(500); // توضیحات تراکنش
                entity.Property(t => t.Timestamp).IsRequired();       // زمان ایجاد رکورد تراکنش

                // --- پیکربندی فیلدهای جدید ---
                entity.Property(t => t.PaymentGatewayInvoiceId)
                    .HasMaxLength(100); // می‌تواند null باشد

                entity.Property(t => t.PaymentGatewayName)
                    .HasMaxLength(50); // می‌تواند null باشد

                entity.Property(t => t.Status)
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasDefaultValue("Pending"); //  مقدار پیش‌فرض برای وضعیت

                entity.Property(t => t.PaidAt); // می‌تواند null باشد

                entity.Property(t => t.PaymentGatewayPayload); // می‌تواند null باشد و طولانی (بسته به نیاز nvarchar(max))

                entity.Property(t => t.PaymentGatewayResponse); // می‌تواند null باشد و طولانی (بسته به نیاز nvarchar(max))


                // تعریف ایندکس برای جستجوی سریع‌تر (اختیاری اما مفید)
                entity.HasIndex(t => t.UserId);
                entity.HasIndex(t => t.PaymentGatewayInvoiceId).IsUnique(false); // ممکن است منحصر به فرد نباشد اگر چندین تلاش برای یک فاکتور باشد، یا null باشد
                entity.HasIndex(t => t.Status);

                // رابطه با User (قبلاً باید وجود داشته باشد)
                entity.HasOne(t => t.User)
                    .WithMany(u => u.Transactions)
                    .HasForeignKey(t => t.UserId)
                    .OnDelete(DeleteBehavior.Cascade); // یا Restrict بسته به نیاز شما
            });

            // --- Data Seeding (اختیاری) ---
            // می‌توانید داده‌های اولیه را در اینجا اضافه کنید
            // modelBuilder.Entity<SignalCategory>().HasData(
            //     new SignalCategory { Id = Guid.NewGuid(), Name = "Forex Major Pairs" },
            //     new SignalCategory { Id = Guid.NewGuid(), Name = "Cryptocurrencies" },
            //     new SignalCategory { Id = Guid.NewGuid(), Name = "Commodities" }
            // );
        }
    }
}