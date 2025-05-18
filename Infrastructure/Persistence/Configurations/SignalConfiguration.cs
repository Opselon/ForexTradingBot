// File: Infrastructure/Persistence/Configurations/SignalConfiguration.cs
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations
{
    public class SignalConfiguration : IEntityTypeConfiguration<Signal>
    {
        public void Configure(EntityTypeBuilder<Signal> builder)
        {
            builder.ToTable("Signals");
            builder.HasKey(s => s.Id);

            builder.Property(s => s.Type)
                .IsRequired()
                .HasConversion(new EnumToStringConverter<SignalType>());

            builder.Property(s => s.Symbol)
                .IsRequired()
                .HasMaxLength(50);
            builder.HasIndex(s => s.Symbol); // For faster queries by symbol

            builder.Property(s => s.EntryPrice).IsRequired().HasColumnType("decimal(18, 8)");
            builder.Property(s => s.StopLoss).IsRequired().HasColumnType("decimal(18, 8)");
            builder.Property(s => s.TakeProfit).IsRequired().HasColumnType("decimal(18, 8)");
            // builder.Property(s => s.TakeProfit2).HasColumnType("decimal(18, 8)"); // If you add more TPs
            // builder.Property(s => s.TakeProfit3).HasColumnType("decimal(18, 8)");

            builder.Property(s => s.SourceProvider) // Renamed from Source for clarity
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(s => s.Status)
                .IsRequired()
                .HasConversion(new EnumToStringConverter<SignalStatus>()) // Assuming you have a SignalStatus enum
                .HasDefaultValue(SignalStatus.Pending);

            builder.Property(s => s.Timeframe).HasMaxLength(10); // e.g., "H1", "D1"
            builder.Property(s => s.Notes).HasMaxLength(1000); // Optional notes for the signal
            builder.Property(s => s.IsVipOnly).IsRequired().HasDefaultValue(false);

            builder.Property(s => s.PublishedAt).IsRequired(); // Renamed from CreatedAt for clarity
            builder.Property(s => s.UpdatedAt);
            builder.Property(s => s.ClosedAt); // When the signal reached TP/SL or was manually closed

            // Foreign Key to SignalCategory
            builder.Property(s => s.CategoryId).IsRequired();
            builder.HasOne(s => s.Category)
                .WithMany(sc => sc.Signals) // Assuming SignalCategory has a 'Signals' collection
                .HasForeignKey(s => s.CategoryId)
                .OnDelete(DeleteBehavior.Restrict); // Don't delete Category if Signals exist

            // Relationship: Signal 1-* SignalAnalyses
            builder.HasMany(s => s.Analyses)
                .WithOne(sa => sa.Signal)
                .HasForeignKey(sa => sa.SignalId)
                .OnDelete(DeleteBehavior.Cascade); // If signal is deleted, its analyses are also deleted

            builder.Property(s => s.SourceProvider) // ✅ نام جدید
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(s => s.Status)
                .IsRequired()
                .HasConversion(new EnumToStringConverter<SignalStatus>()) // ✅ استفاده از EnumToStringConverter
                .HasDefaultValue(SignalStatus.Pending);

            builder.Property(s => s.Timeframe).HasMaxLength(10);
            builder.Property(s => s.Notes).HasMaxLength(1000);
            builder.Property(s => s.IsVipOnly).IsRequired().HasDefaultValue(false);

            builder.Property(s => s.PublishedAt).IsRequired(); // ✅ نام جدید
            builder.Property(s => s.UpdatedAt);
            builder.Property(s => s.ClosedAt);
        }
    }
}