// File: Infrastructure/Persistence/Configurations/TransactionConfiguration.cs
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations
{
    public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
    {
        public void Configure(EntityTypeBuilder<Transaction> builder)
        {
            _ = builder.ToTable("Transactions");
            _ = builder.HasKey(t => t.Id);

            _ = builder.Property(t => t.UserId).IsRequired();

            _ = builder.Property(t => t.Amount)
                .IsRequired()
                .HasColumnType("decimal(18, 4)"); // Precision for transaction amounts

            _ = builder.Property(t => t.Type)
                .IsRequired()
                .HasConversion(new EnumToStringConverter<TransactionType>());

            _ = builder.Property(t => t.Description)
                .HasMaxLength(500); // Optional

            _ = builder.Property(t => t.Timestamp) // Renamed from CreatedAt in your previous DbContext to avoid confusion with audit fields
                .IsRequired();

            // New fields for payment gateway integration
            _ = builder.Property(t => t.PaymentGatewayInvoiceId)
                .HasMaxLength(100);
            _ = builder.Property(t => t.PaymentGatewayName)
                .HasMaxLength(50);

            _ = builder.Property(t => t.Status)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("Pending");

            _ = builder.Property(t => t.PaidAt); // Nullable DateTime

            _ = builder.Property(t => t.PaymentGatewayPayload)
                .HasColumnType("nvarchar(max)"); // For potentially long JSON strings

            _ = builder.Property(t => t.PaymentGatewayResponse)
                .HasColumnType("nvarchar(max)"); // For potentially long JSON strings

            // Indexes
            _ = builder.HasIndex(t => t.UserId);
            _ = builder.HasIndex(t => t.PaymentGatewayInvoiceId); // Not necessarily unique if retries create new internal transactions for same gateway ID
            _ = builder.HasIndex(t => t.Status);
            _ = builder.HasIndex(t => t.Timestamp);
        }
    }
}