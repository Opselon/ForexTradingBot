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
            builder.ToTable("NewsItems");
            builder.HasKey(ni => ni.Id);
            builder.Property(ni => ni.Id).ValueGeneratedOnAdd();

            builder.Property(ni => ni.Title).IsRequired().HasMaxLength(500);
            builder.Property(ni => ni.Link).IsRequired().HasMaxLength(2083);
            builder.HasIndex(ni => ni.Link).IsUnique(false);

            builder.Property(ni => ni.Summary).HasColumnType("nvarchar(max)");
            builder.Property(ni => ni.FullContent).HasColumnType("nvarchar(max)");
            builder.Property(ni => ni.ImageUrl).HasMaxLength(2083);

            builder.Property(ni => ni.PublishedDate).IsRequired(); // اگر تاریخ انتشار همیشه از منبع می‌آید
            builder.Property(ni => ni.CreatedAt).IsRequired(); //.HasDefaultValueSql("GETUTCDATE()");

            builder.Property(ni => ni.LastProcessedAt);

            // ✅ پیکربندی فیلدهای جدید
            builder.Property(ni => ni.SourceName)
                .HasMaxLength(150); // می‌تواند null باشد

            builder.Property(ni => ni.SourceItemId)
                .HasMaxLength(500); // می‌تواند null باشد

            // ✅ ایندکس ترکیبی بسیار مهم برای جلوگیری از تکرار
            // این ایندکس اطمینان حاصل می‌کند که ترکیب RssSourceId و SourceItemId منحصر به فرد است،
            // اما فقط زمانی که SourceItemId مقدار دارد (برای سازگاری با آیتم‌هایی که ممکن است SourceItemId نداشته باشند).
            builder.HasIndex(ni => new { ni.RssSourceId, ni.SourceItemId })
                   .IsUnique()
                   .HasFilter("[SourceItemId] IS NOT NULL"); // فیلتر برای SQL Server
                                                             // برای PostgreSQL: .HasFilter("\"SourceItemId\" IS NOT NULL")

            builder.Property(ni => ni.SentimentScore);
            builder.Property(ni => ni.SentimentLabel).HasMaxLength(50);
            builder.Property(ni => ni.DetectedLanguage).HasMaxLength(10);
            builder.Property(ni => ni.AffectedAssets).HasMaxLength(500);

            // رابطه با RssSource
            builder.Property(ni => ni.RssSourceId).IsRequired(); // اطمینان از اینکه این هم Required است
            builder.HasOne(ni => ni.RssSource)
                   .WithMany(rs => rs.NewsItems)
                   .HasForeignKey(ni => ni.RssSourceId)
                   .OnDelete(DeleteBehavior.Cascade); // یا Restrict/SetNull
        }
    }
}