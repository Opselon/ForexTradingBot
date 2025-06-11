// File: Infrastructure/Persistence/Configurations/NewsItemConfiguration.cs
#region Usings
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
#endregion

namespace Infrastructure.Persistence.Configurations
{
    public class NewsItemConfiguration : IEntityTypeConfiguration<NewsItem>
    {
        public void Configure(EntityTypeBuilder<NewsItem> builder)
        {
            _ = builder.ToTable("NewsItems");
            _ = builder.HasKey(ni => ni.Id);
            _ = builder.Property(ni => ni.Id).ValueGeneratedOnAdd();

            _ = builder.Property(ni => ni.Title).IsRequired().HasMaxLength(500);
            _ = builder.Property(ni => ni.Link).IsRequired().HasMaxLength(2083);
            _ = builder.HasIndex(ni => ni.Link).IsUnique(false);

            _ = builder.Property(ni => ni.Summary).HasColumnType("nvarchar(max)");
            _ = builder.Property(ni => ni.FullContent).HasColumnType("nvarchar(max)");
            _ = builder.Property(ni => ni.ImageUrl).HasMaxLength(2083);

            _ = builder.Property(ni => ni.PublishedDate).IsRequired(); // اگر تاریخ انتشار همیشه از منبع می‌آید
            _ = builder.Property(ni => ni.CreatedAt).IsRequired(); //.HasDefaultValueSql("GETUTCDATE()");

            _ = builder.Property(ni => ni.LastProcessedAt);

            // ✅ پیکربندی فیلدهای جدید
            _ = builder.Property(ni => ni.SourceName)
                .HasMaxLength(150); // می‌تواند null باشد

            _ = builder.Property(ni => ni.SourceItemId)
                .HasMaxLength(500); // می‌تواند null باشد

            // ✅ ایندکس ترکیبی بسیار مهم برای جلوگیری از تکرار
            // این ایندکس اطمینان حاصل می‌کند که ترکیب RssSourceId و SourceItemId منحصر به فرد است،
            // اما فقط زمانی که SourceItemId مقدار دارد (برای سازگاری با آیتم‌هایی که ممکن است SourceItemId نداشته باشند).
            _ = builder.HasIndex(ni => new { ni.RssSourceId, ni.SourceItemId })
                   .IsUnique()
                   .HasFilter("[SourceItemId] IS NOT NULL"); // فیلتر برای SQL Server
                                                             // برای PostgreSQL: .HasFilter("\"SourceItemId\" IS NOT NULL")

            _ = builder.Property(ni => ni.SentimentScore);
            _ = builder.Property(ni => ni.SentimentLabel).HasMaxLength(50);
            _ = builder.Property(ni => ni.DetectedLanguage).HasMaxLength(10);
            _ = builder.Property(ni => ni.AffectedAssets).HasMaxLength(500);

            // رابطه با RssSource
            _ = builder.Property(ni => ni.RssSourceId).IsRequired(); // اطمینان از اینکه این هم Required است
            _ = builder.HasOne(ni => ni.RssSource)
                   .WithMany(rs => rs.NewsItems)
                   .HasForeignKey(ni => ni.RssSourceId)
                   .OnDelete(DeleteBehavior.Cascade); // یا Restrict/SetNull

            _ = builder.Property(ni => ni.IsVipOnly).HasDefaultValue(false);
            _ = builder.Property(ni => ni.AssociatedSignalCategoryId);
            _ = builder.HasOne(ni => ni.AssociatedSignalCategory)
                .WithMany() // فرض: SignalCategory نویگیشن برعکس به NewsItem ندارد
                .HasForeignKey(ni => ni.AssociatedSignalCategoryId)
                .OnDelete(DeleteBehavior.SetNull);

        }
    }
}