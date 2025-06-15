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
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

        public void Configure(EntityTypeBuilder<ForwardingRule> builder)
        {
            _ = builder.ToTable("ForwardingRules");
            _ = builder.HasKey(fr => fr.RuleName);

            _ = builder.Property(fr => fr.RuleName)
                .IsRequired()
                .HasMaxLength(100);

            _ = builder.Property(fr => fr.IsEnabled)
                .IsRequired();

            _ = builder.Property(fr => fr.SourceChannelId)
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
            _ = builder.OwnsOne(fr => fr.EditOptions, editOptions =>
            {
                _ = editOptions.Property(e => e.PrependText);
                _ = editOptions.Property(e => e.AppendText);
                _ = editOptions.Property(e => e.RemoveSourceForwardHeader);
                _ = editOptions.Property(e => e.RemoveLinks);
                _ = editOptions.Property(e => e.StripFormatting);
                _ = editOptions.Property(e => e.CustomFooter);
                _ = editOptions.Property(e => e.DropAuthor);
                _ = editOptions.Property(e => e.DropMediaCaptions);
                _ = editOptions.Property(e => e.NoForwards);

                // OwnsMany برای TextReplacements - استفاده از نام کلاس TextReplacement (جدید)
                _ = editOptions.OwnsMany(e => e.TextReplacements, tr =>
                {
                    _ = tr.WithOwner().HasForeignKey("EditOptionsForwardingRuleName"); // باید این نام با نام FK در دیتابیس هماهنگ باشه
                    _ = tr.Property<int>("Id").ValueGeneratedOnAdd();
                    _ = tr.HasKey("Id", "EditOptionsForwardingRuleName"); // کلید ترکیبی
                    _ = tr.Property(t => t.Find).IsRequired();
                    _ = tr.Property(t => t.ReplaceWith);
                    _ = tr.Property(t => t.IsRegex).IsRequired();
                    _ = tr.Property(t => t.RegexOptions).IsRequired(); // System.Text.RegularExpressions.RegexOptions enum value
                });
            });

            // OwnerOne برای MessageFilterOptions
            _ = builder.OwnsOne(fr => fr.FilterOptions, filterOptions =>
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

                _ = filterOptions.Property(f => f.ContainsText);
                _ = filterOptions.Property(f => f.ContainsTextIsRegex);
                _ = filterOptions.Property(f => f.ContainsTextRegexOptions);
                _ = filterOptions.Property(f => f.IgnoreEditedMessages);
                _ = filterOptions.Property(f => f.IgnoreServiceMessages);
                _ = filterOptions.Property(f => f.MaxMessageLength);
                _ = filterOptions.Property(f => f.MinMessageLength);
            });
        }
    }
}