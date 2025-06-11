// File: Infrastructure/Persistence/Configurations/SubscriptionConfiguration.cs
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations
{
    public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
    {
        public void Configure(EntityTypeBuilder<Subscription> builder)
        {
            _ = builder.ToTable("Subscriptions");
            _ = builder.HasKey(s => s.Id);

            _ = builder.Property(s => s.UserId).IsRequired(); // FK to User
            _ = builder.Property(s => s.StartDate).IsRequired();
            _ = builder.Property(s => s.EndDate).IsRequired();
            _ = builder.Property(s => s.CreatedAt).IsRequired();
            _ = builder.Property(s => s.UpdatedAt); // Nullable

            // IsActive is a calculated property in the entity, so it should be ignored by EF Core
            _ = builder.Ignore(s => s.IsCurrentlyActive);

            // If Subscription relates to a SubscriptionPlan entity:
            // builder.Property(s => s.PlanId).IsRequired();
            // builder.HasOne(s => s.Plan) // Assuming a Navigation Property 'Plan' of type SubscriptionPlan
            //        .WithMany() // Or .WithMany(p => p.Subscriptions) if Plan has a collection of Subscriptions
            //        .HasForeignKey(s => s.PlanId)
            //        .OnDelete(DeleteBehavior.Restrict); // Don't delete a plan if subscriptions exist

            // Index on UserId for faster querying of a user's subscriptions
            _ = builder.HasIndex(s => s.UserId);
            // Index on EndDate for querying active/expired subscriptions
            _ = builder.HasIndex(s => s.EndDate);
        }
    }
}