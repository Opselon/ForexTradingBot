using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Collections.Generic;
using System.Text.Json;

namespace Infrastructure.Persistence.Configurations
{
    public class ForwardingRuleConfiguration : IEntityTypeConfiguration<ForwardingRule>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions();

        public void Configure(EntityTypeBuilder<ForwardingRule> builder)
        {
            builder.ToTable("ForwardingRules");
            builder.HasKey(fr => fr.RuleName);

            builder.Property(fr => fr.RuleName)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(fr => fr.IsEnabled)
                .IsRequired();

            builder.Property(fr => fr.SourceChannelId)
                .IsRequired();

            builder.Property(fr => fr.TargetChannelIds)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, _jsonOptions),
                    v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                );

            builder.OwnsOne(fr => fr.EditOptions, editOptions =>
            {
                editOptions.Property(e => e.PrependText);
                editOptions.Property(e => e.AppendText);
                editOptions.Property(e => e.RemoveSourceForwardHeader);
                editOptions.Property(e => e.RemoveLinks);
                editOptions.Property(e => e.StripFormatting);
                editOptions.Property(e => e.CustomFooter);
                editOptions.Property(e => e.DropAuthor);
                editOptions.Property(e => e.DropMediaCaptions);
                editOptions.Property(e => e.NoForwards);
                editOptions.OwnsMany(e => e.TextReplacements, tr =>
                {
                    tr.Property(t => t.Find);
                    tr.Property(t => t.ReplaceWith);
                    tr.Property(t => t.IsRegex);
                    tr.Property(t => t.RegexOptions);
                });
            });

            builder.OwnsOne(fr => fr.FilterOptions, filterOptions =>
            {
                filterOptions.Property(f => f.ContainsText);
                filterOptions.Property(f => f.ContainsTextIsRegex);
                filterOptions.Property(f => f.ContainsTextRegexOptions);
                filterOptions.Property(f => f.IgnoreEditedMessages);
                filterOptions.Property(f => f.IgnoreServiceMessages);
                filterOptions.Property(f => f.MinMessageLength);
                filterOptions.Property(f => f.MaxMessageLength);
                filterOptions.Property(f => f.AllowedMessageTypes)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, _jsonOptions),
                        v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>()
                    );
                filterOptions.Property(f => f.AllowedMimeTypes)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, _jsonOptions),
                        v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>()
                    );
                filterOptions.Property(f => f.AllowedSenderUserIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, _jsonOptions),
                        v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                    );
                filterOptions.Property(f => f.BlockedSenderUserIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, _jsonOptions),
                        v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                    );
            });
        }
    }
} 