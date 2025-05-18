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
            builder.ToTable("Subscriptions");
            builder.HasKey(s => s.Id);

            builder.Property(s => s.UserId).IsRequired(); // FK to User
            builder.Property(s => s.StartDate).IsRequired();
            builder.Property(s => s.EndDate).IsRequired();
            builder.Property(s => s.CreatedAt).IsRequired();
            builder.Property(s => s.UpdatedAt); // Nullable

            // IsActive is a calculated property in the entity, so it should be ignored by EF Core
            builder.Ignore(s => s.IsCurrentlyActive);

            // If Subscription relates to a SubscriptionPlan entity:
            // builder.Property(s => s.PlanId).IsRequired();
            // builder.HasOne(s => s.Plan) // Assuming a Navigation Property 'Plan' of type SubscriptionPlan
            //        .WithMany() // Or .WithMany(p => p.Subscriptions) if Plan has a collection of Subscriptions
            //        .HasForeignKey(s => s.PlanId)
            //        .OnDelete(DeleteBehavior.Restrict); // Don't delete a plan if subscriptions exist

            // Index on UserId for faster querying of a user's subscriptions
            builder.HasIndex(s => s.UserId);
            // Index on EndDate for querying active/expired subscriptions
            builder.HasIndex(s => s.EndDate);
        }
    }
}