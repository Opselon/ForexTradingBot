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
            builder.ToTable("Transactions");
            builder.HasKey(t => t.Id);

            builder.Property(t => t.UserId).IsRequired();

            builder.Property(t => t.Amount)
                .IsRequired()
                .HasColumnType("decimal(18, 4)"); // Precision for transaction amounts

            builder.Property(t => t.Type)
                .IsRequired()
                .HasConversion(new EnumToStringConverter<TransactionType>());

            builder.Property(t => t.Description)
                .HasMaxLength(500); // Optional

            builder.Property(t => t.Timestamp) // Renamed from CreatedAt in your previous DbContext to avoid confusion with audit fields
                .IsRequired();

            // New fields for payment gateway integration
            builder.Property(t => t.PaymentGatewayInvoiceId)
                .HasMaxLength(100);
            builder.Property(t => t.PaymentGatewayName)
                .HasMaxLength(50);

            builder.Property(t => t.Status)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("Pending");

            builder.Property(t => t.PaidAt); // Nullable DateTime

            builder.Property(t => t.PaymentGatewayPayload)
                .HasColumnType("nvarchar(max)"); // For potentially long JSON strings

            builder.Property(t => t.PaymentGatewayResponse)
                .HasColumnType("nvarchar(max)"); // For potentially long JSON strings

            // Indexes
            builder.HasIndex(t => t.UserId);
            builder.HasIndex(t => t.PaymentGatewayInvoiceId); // Not necessarily unique if retries create new internal transactions for same gateway ID
            builder.HasIndex(t => t.Status);
            builder.HasIndex(t => t.Timestamp);
        }
    }
}