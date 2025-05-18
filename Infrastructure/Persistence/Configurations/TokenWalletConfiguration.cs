// File: Infrastructure/Persistence/Configurations/TokenWalletConfiguration.cs
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations
{
    public class TokenWalletConfiguration : IEntityTypeConfiguration<TokenWallet>
    {
        public void Configure(EntityTypeBuilder<TokenWallet> builder)
        {
            builder.ToTable("TokenWallets");
            builder.HasKey(tw => tw.Id);

            // UserId (FK to User) is configured by the User entity's HasOne relationship.
            // We just need to ensure it's required if not handled by User's config.
            // builder.Property(tw => tw.UserId).IsRequired(); // This is implicitly handled by the User-TokenWallet relationship

            builder.Property(tw => tw.Balance)
                .IsRequired()
                .HasColumnType("decimal(18, 8)") // High precision for token balance
                .HasDefaultValue(0m);

            // builder.Property(tw => tw.CurrencyCode).HasMaxLength(10); // If using CurrencyCode

            builder.Property(tw => tw.IsActive)
                .IsRequired()
                .HasDefaultValue(true);

            builder.Property(tw => tw.CreatedAt)
                .IsRequired();
            builder.Property(tw => tw.UpdatedAt)
                .IsRequired();

            // builder.Property(tw => tw.LastTransactionDate); // If using LastTransactionDate

            // Unique index on UserId to enforce one-to-one (if not handled by PK on User side)
            // This is also covered by the HasForeignKey<TokenWallet>(tw => tw.UserId) in UserConfiguration
            // builder.HasIndex(tw => tw.UserId).IsUnique();
        }
    }
}