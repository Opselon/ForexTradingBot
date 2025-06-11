// File: Infrastructure\Data\Configurations\ForwardingRuleConfiguration.cs

#region Usings
using Domain.Features.Forwarding.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;
#endregion

namespace Infrastructure.Persistence.Configurations // یا Infrastructure.Data.Configurations اگر ساختارتان متفاوت است
{
    public class ForwardingRuleConfiguration : IEntityTypeConfiguration<ForwardingRule>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = false };

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

            // برای TargetChannelIds (IReadOnlyList<long>)
            builder.Property(fr => fr.TargetChannelIds)
                .HasConversion(
                    v => JsonSerializer.Serialize(v ?? new List<long>(), _jsonOptions),
                    v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                )
                .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                    (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
                    (c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                    (c) => c.ToList() // Snapshot: Create a new list for tracking changes
                ));

            // OwnerOne برای MessageEditOptions
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

                // OwnsMany برای TextReplacements - استفاده از نام کلاس TextReplacement (جدید)
                editOptions.OwnsMany(e => e.TextReplacements, tr =>
                {
                    tr.WithOwner().HasForeignKey("EditOptionsForwardingRuleName"); // باید این نام با نام FK در دیتابیس هماهنگ باشه
                    tr.Property<int>("Id").ValueGeneratedOnAdd();
                    tr.HasKey("Id", "EditOptionsForwardingRuleName"); // کلید ترکیبی
                    tr.Property(t => t.Find).IsRequired();
                    tr.Property(t => t.ReplaceWith);
                    tr.Property(t => t.IsRegex).IsRequired();
                    tr.Property(t => t.RegexOptions).IsRequired(); // System.Text.RegularExpressions.RegexOptions enum value
                });
            });

            // OwnerOne برای MessageFilterOptions
            builder.OwnsOne(fr => fr.FilterOptions, filterOptions =>
            {
                filterOptions.Property(f => f.AllowedMessageTypes)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<string>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                        (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
                        (c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v == null ? 0 : v.GetHashCode())),
                        (c) => c.ToList()
                    ));

                filterOptions.Property(f => f.AllowedMimeTypes)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<string>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<string>>(v, _jsonOptions) ?? new List<string>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                        (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
                        (c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v == null ? 0 : v.GetHashCode())),
                        (c) => c.ToList()
                    ));

                filterOptions.Property(f => f.AllowedSenderUserIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<long>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                        (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
                        (c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        (c) => c.ToList()
                    ));

                filterOptions.Property(f => f.BlockedSenderUserIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v ?? new List<long>(), _jsonOptions),
                        v => JsonSerializer.Deserialize<List<long>>(v, _jsonOptions) ?? new List<long>()
                    )
                    .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                        (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
                        (c) => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        (c) => c.ToList()
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