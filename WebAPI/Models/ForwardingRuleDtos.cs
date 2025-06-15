using Domain.Features.Forwarding.ValueObjects;
using System.Text.RegularExpressions;

namespace WebAPI.Models
{
    // DTO for the main POST/PUT request body
    public class ForwardingRuleDto
    {
        public required string RuleName { get; set; }
        public bool IsEnabled { get; set; }
        public long SourceChannelId { get; set; }
        public required List<long> TargetChannelIds { get; set; }
        public required MessageEditOptionsDto EditOptions { get; set; }
        public required MessageFilterOptionsDto FilterOptions { get; set; }
    }

    // DTO for the nested MessageEditOptions
    public class MessageEditOptionsDto
    {
        public string? PrependText { get; set; }
        public string? AppendText { get; set; }
        public bool RemoveSourceForwardHeader { get; set; }
        public bool RemoveLinks { get; set; }
        public bool StripFormatting { get; set; }
        public string? CustomFooter { get; set; }
        public bool DropAuthor { get; set; }
        public bool DropMediaCaptions { get; set; }
        public bool NoForwards { get; set; }

        // We reuse the TextReplacement Value Object directly as it is a simple data carrier.
        // This is acceptable. If it had complex logic, it would need its own DTO.
        public required List<TextReplacement> TextReplacements { get; set; }
    }
    // In WebAPI/Models/ForwardingRuleDtos.cs

    // Add this new DTO for GET requests to avoid exposing the full domain model
    public class ForwardingRuleSummaryDto
    {
        public required string RuleName { get; set; }
        public bool IsEnabled { get; set; }
        public long SourceChannelId { get; set; }
        public required List<long> TargetChannelIds { get; set; }
    }


    // DTO for the nested MessageFilterOptions
    public class MessageFilterOptionsDto
    {
        public required List<string> AllowedMessageTypes { get; set; }
        public required List<string> AllowedMimeTypes { get; set; }
        public required List<long> AllowedSenderUserIds { get; set; }
        public required List<long> BlockedSenderUserIds { get; set; }
        public string? ContainsText { get; set; }
        public bool ContainsTextIsRegex { get; set; }
        public RegexOptions ContainsTextRegexOptions { get; set; }
        public bool IgnoreEditedMessages { get; set; }
        public bool IgnoreServiceMessages { get; set; }
        public int? MaxMessageLength { get; set; }
        public int? MinMessageLength { get; set; }
    }
}