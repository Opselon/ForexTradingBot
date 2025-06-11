// File: Infrastructure/Persistence/Configurations/UserConfiguration.cs
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations
{
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            _ = builder.ToTable("Users"); // نام جدول

            _ = builder.HasKey(u => u.Id); // کلید اصلی

            // Username
            _ = builder.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(100);
            _ = builder.HasIndex(u => u.Username)
                .IsUnique();

            // TelegramId
            _ = builder.Property(u => u.TelegramId)
                .IsRequired()
                .HasMaxLength(50); // معمولاً شناسه عددی است اما به عنوان رشته ذخیره می‌شود
            _ = builder.HasIndex(u => u.TelegramId)
                .IsUnique();

            // Email
            _ = builder.Property(u => u.Email)
                .IsRequired()
                .HasMaxLength(200);
            _ = builder.HasIndex(u => u.Email)
                .IsUnique();

            // Level (Enum to String)
            _ = builder.Property(u => u.Level)
                .IsRequired()
                .HasConversion(new EnumToStringConverter<UserLevel>());

            // Timestamps
            _ = builder.Property(u => u.CreatedAt)
                .IsRequired();
            _ = builder.Property(u => u.UpdatedAt); // Nullable

            // Notification Settings with Default Values
            _ = builder.Property(u => u.EnableGeneralNotifications)
                .IsRequired()
                .HasDefaultValue(true);
            _ = builder.Property(u => u.EnableVipSignalNotifications)
                .IsRequired()
                .HasDefaultValue(false); // پیش‌فرض false برای VIP
            _ = builder.Property(u => u.EnableRssNewsNotifications)
                .IsRequired()
                .HasDefaultValue(true);

            // Language Preference
            _ = builder.Property(u => u.PreferredLanguage)
                .IsRequired()
                .HasMaxLength(10)
                .HasDefaultValue("en");

            // --- Relationships ---
            // User 1-1 TokenWallet (TokenWallet has the FK)
            _ = builder.HasOne(u => u.TokenWallet)
                .WithOne(tw => tw.User)
                .HasForeignKey<TokenWallet>(tw => tw.UserId) // FK is in TokenWallet
                .OnDelete(DeleteBehavior.Cascade); // If user is deleted, their wallet is also deleted

            // User 1-* Subscriptions
            _ = builder.HasMany(u => u.Subscriptions)
                .WithOne(s => s.User)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // User 1-* Transactions
            _ = builder.HasMany(u => u.Transactions)
                .WithOne(t => t.User)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // User 1-* UserSignalPreferences
            _ = builder.HasMany(u => u.Preferences)
                .WithOne(usp => usp.User)
                .HasForeignKey(usp => usp.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}