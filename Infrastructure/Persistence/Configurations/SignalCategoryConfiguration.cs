// File: Infrastructure/Persistence/Configurations/SignalCategoryConfiguration.cs
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations
{
    public class SignalCategoryConfiguration : IEntityTypeConfiguration<SignalCategory>
    {
        public void Configure(EntityTypeBuilder<SignalCategory> builder)
        {
            _ = builder.ToTable("SignalCategories");
            _ = builder.HasKey(sc => sc.Id);

            _ = builder.Property(sc => sc.Name)
                .IsRequired()
                .HasMaxLength(100);
            _ = builder.HasIndex(sc => sc.Name)
                .IsUnique(); // Category names should be unique

            _ = builder.Property(sc => sc.Description).HasMaxLength(500); // Optional description

            // Relationship: SignalCategory 1-* Signals (Signal has the FK to Category)
            // This side of the relationship is typically configured by the 'Signal' entity's configuration
            // if 'Signal' has a navigation property back to 'SignalCategory'.
            // If 'SignalCategory' has a collection of 'Signals', it's configured here.
            // builder.HasMany(sc => sc.Signals)
            //        .WithOne(s => s.Category) // Assuming Signal has a 'Category' navigation property
            //        .HasForeignKey(s => s.CategoryId)
            //        .OnDelete(DeleteBehavior.Restrict); // Don't delete category if signals are linked
            // This is already defined in your AppDbContext, so it's consistent.
        }
    }
}