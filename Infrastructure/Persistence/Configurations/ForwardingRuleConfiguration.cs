#region Usings
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions; // Crucial for Expression<Func<...>>
using System.Text.Json;
using System;
#endregion

namespace Infrastructure.Persistence.Configurations
{
    public class ForwardingRuleConfiguration : IEntityTypeConfiguration<ForwardingRule>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions();

        // Note: The helper methods CreateLongListSnapshot/CreateStringListSnapshot as previously defined
        // (taking T? and returning T?) are not directly suitable if the expression needs Func<T, T>.
        // We can inline the ToList() logic or make new helpers if preferred for more complex scenarios.

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

            // For TargetChannelIds (IReadOnlyList<long>)
            builder.Property(fr => fr.TargetChannelIds)
                .HasConversion(
                    v => JsonSerializer.Serialize(v ?? new List<long>(), _jsonOptions),
                    v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                )
                .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                    // Equals: Takes IReadOnlyList<long>?
                    (Expression<Func<IReadOnlyList<long>?, IReadOnlyList<long>?, bool>>)(
                        (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2))),

                    // HashCode: Takes IReadOnlyList<long> (non-nullable)
                    (Expression<Func<IReadOnlyList<long>, int>>)(
                        (IReadOnlyList<long> c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode()))),

                    // Snapshot: Takes IReadOnlyList<long> (non-nullable), returns IReadOnlyList<long> (non-nullable)
                    (Expression<Func<IReadOnlyList<long>, IReadOnlyList<long>>>)(
                        (IReadOnlyList<long> c) => c.ToList())
                ));

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
                    tr.WithOwner().HasForeignKey("EditOptionsForwardingRuleName");
                    tr.Property<int>("Id").ValueGeneratedOnAdd();
                    tr.HasKey("Id", "EditOptionsForwardingRuleName");
                    tr.Property(t => t.Find);
                    tr.Property(t => t.ReplaceWith);
                    tr.Property(t => t.IsRegex);
                    tr.Property(t => t.RegexOptions);
                });
            });

            builder.OwnsOne(fr => fr.FilterOptions, filterOptions =>
            {
                // For AllowedMessageTypes (IReadOnlyList<string>)
                filterOptions.Property(f => f.AllowedMessageTypes)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<string>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                        (Expression<Func<IReadOnlyList<string>?, IReadOnlyList<string>?, bool>>)(
                            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2))),
                        (Expression<Func<IReadOnlyList<string>, int>>)(
                            (IReadOnlyList<string> c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v == null ? 0 : v.GetHashCode()))),
                        (Expression<Func<IReadOnlyList<string>, IReadOnlyList<string>>>)(
                            (IReadOnlyList<string> c) => c.ToList())
                    ));

                // For AllowedMimeTypes (IReadOnlyList<string>)
                filterOptions.Property(f => f.AllowedMimeTypes)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<string>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                        (Expression<Func<IReadOnlyList<string>?, IReadOnlyList<string>?, bool>>)(
                            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2))),
                        (Expression<Func<IReadOnlyList<string>, int>>)(
                            (IReadOnlyList<string> c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v == null ? 0 : v.GetHashCode()))),
                        (Expression<Func<IReadOnlyList<string>, IReadOnlyList<string>>>)(
                            (IReadOnlyList<string> c) => c.ToList())
                    ));

                // For AllowedSenderUserIds (IReadOnlyList<long>)
                filterOptions.Property(f => f.AllowedSenderUserIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<long>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                        (Expression<Func<IReadOnlyList<long>?, IReadOnlyList<long>?, bool>>)(
                            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2))),
                        (Expression<Func<IReadOnlyList<long>, int>>)(
                             (IReadOnlyList<long> c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode()))),
                        (Expression<Func<IReadOnlyList<long>, IReadOnlyList<long>>>)(
                            (IReadOnlyList<long> c) => c.ToList())
                    ));

                // For BlockedSenderUserIds (IReadOnlyList<long>)
                filterOptions.Property(f => f.BlockedSenderUserIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<long>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                        (Expression<Func<IReadOnlyList<long>?, IReadOnlyList<long>?, bool>>)(
                            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2))),
                        (Expression<Func<IReadOnlyList<long>, int>>)(
                            (IReadOnlyList<long> c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode()))),
                        (Expression<Func<IReadOnlyList<long>, IReadOnlyList<long>>>)(
                            (IReadOnlyList<long> c) => c.ToList())
                    ));

                filterOptions.Property(f => f.ContainsText);
                filterOptions.Property(f => f.ContainsTextIsRegex);
                filterOptions.Property(f => f.ContainsTextRegexOptions);
                filterOptions.Property(f => f.IgnoreEditedMessages);
                filterOptions.Property(f => f.IgnoreServiceMessages);
                filterOptions.Property(f => f.MaxMessageLength);
                filterOptions.Property(f => f.MinMessageLength);
            });
        }
    }
}