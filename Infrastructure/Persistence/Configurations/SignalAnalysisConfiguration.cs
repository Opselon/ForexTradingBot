// File: Infrastructure/Persistence/Configurations/SignalAnalysisConfiguration.cs
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations
{
    public class SignalAnalysisConfiguration : IEntityTypeConfiguration<SignalAnalysis>
    {
        public void Configure(EntityTypeBuilder<SignalAnalysis> builder)
        {
            builder.ToTable("SignalAnalyses");
            builder.HasKey(sa => sa.Id);

            builder.Property(sa => sa.SignalId).IsRequired(); // FK to Signal

            builder.Property(sa => sa.AnalystName)
                .IsRequired()
                .HasMaxLength(150);

            builder.Property(sa => sa.AnalysisText) // Renamed from Notes for clarity
                .IsRequired()
                .HasColumnType("nvarchar(max)"); // For potentially long analysis text

            builder.Property(sa => sa.SentimentScore); // Nullable double or decimal
            // builder.Property(sa => sa.AnalysisType).HasMaxLength(50); // e.g., "Technical", "Fundamental", "AI_Sentiment"

            builder.Property(sa => sa.CreatedAt).IsRequired();

            // Relationship with Signal is configured by Signal entity's HasMany
        }
    }
}