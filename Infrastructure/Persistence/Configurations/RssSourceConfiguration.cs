// File: Infrastructure/Persistence/Configurations/RssSourceConfiguration.cs
#region Usings
using Domain.Entities; // برای RssSource, SignalCategory, NewsItem
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
#endregion

namespace Infrastructure.Persistence.Configurations
{
    /// <summary>
    /// پیکربندی Entity Framework Core برای موجودیت RssSource.
    /// </summary>
    public class RssSourceConfiguration : IEntityTypeConfiguration<RssSource>
    {
        public void Configure(EntityTypeBuilder<RssSource> builder)
        {
            _ = builder.ToTable("RssSources");
            _ = builder.HasKey(rs => rs.Id);
            _ = builder.Property(rs => rs.Id).ValueGeneratedOnAdd();

            _ = builder.Property(rs => rs.Url)
                .IsRequired()
                .HasMaxLength(2083); //  استفاده از طول استاندارد URL
            _ = builder.HasIndex(rs => rs.Url).IsUnique(); // URL باید منحصر به فرد باشد

            _ = builder.Property(rs => rs.SourceName)
                .IsRequired()
                .HasMaxLength(150);
            _ = builder.HasIndex(rs => rs.SourceName); //  ایندکس برای جستجو بر اساس نام (اختیاری)

            _ = builder.Property(rs => rs.IsActive).IsRequired().HasDefaultValue(true);
            _ = builder.Property(rs => rs.CreatedAt).IsRequired().HasDefaultValueSql("GETUTCDATE()"); // یا NOW()
            _ = builder.Property(rs => rs.UpdatedAt); // می‌تواند null باشد

            // --- پیکربندی فیلدهای جدید RSS ---
            _ = builder.Property(rs => rs.LastModifiedHeader) // ✅ از کد شما
                .HasMaxLength(100);

            _ = builder.Property(rs => rs.ETag) // ✅ از کد شما
                .HasMaxLength(255);

            // builder.Property(rs => rs.LastFetchAttemptAt); // این فیلد در مدل RssSource شما نبود، اگر اضافه کردید، اینجا هم اضافه کنید
            _ = builder.Property(rs => rs.LastSuccessfulFetchAt); // ✅ از کد شما (قبلاً LastSuccessfulFetchAt نامیده بودم، با مدل شما هماهنگ می‌کنم)

            _ = builder.Property(rs => rs.FetchIntervalMinutes); // ✅ از کد شما (می‌تواند null باشد)

            _ = builder.Property(rs => rs.FetchErrorCount) // ✅ از کد شما
                .IsRequired()
                .HasDefaultValue(0);

            _ = builder.Property(rs => rs.Description) // ✅ از کد شما
                .HasMaxLength(1000);

            _ = builder.Property(rs => rs.DefaultSignalCategoryId); // ✅ از کد شما (می‌تواند null باشد)


            // --- تعریف روابط ---

            // رابطه یک-به-چند با NewsItem: هر RssSource می‌تواند چندین NewsItem داشته باشد.
            // این رابطه از سمت NewsItem (با HasOne) تعریف شده است، پس اینجا فقط کالکشن را داریم.
            // اگر بخواهید از این سمت هم تعریف کنید (اختیاری):
            // builder.HasMany(rs => rs.NewsItems)
            //        .WithOne(ni => ni.RssSource)
            //        .HasForeignKey(ni => ni.RssSourceId)
            //        .OnDelete(DeleteBehavior.Cascade); //  این با تعریف در NewsItemConfiguration همخوانی دارد

            // رابطه اختیاری با SignalCategory (برای DefaultSignalCategoryId)
            _ = builder.HasOne(rs => rs.DefaultSignalCategory) // ✅ از کد شما
                   .WithMany() //  فرض می‌کنیم یک SignalCategory می‌تواند پیش‌فرض چندین RssSource باشد
                               //  و SignalCategory پراپرتی نویگیشن برعکس به RssSource ندارد.
                   .HasForeignKey(rs => rs.DefaultSignalCategoryId)
                   .OnDelete(DeleteBehavior.SetNull); // اگر دسته‌بندی حذف شد، DefaultSignalCategoryId در RssSource به null تغییر کند
        }
    }
}