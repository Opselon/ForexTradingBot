using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Infrastructure.Settings
{
    public class ForwardingRule
    {
        public string RuleName { get; set; } = "DefaultRule";
        public bool IsEnabled { get; set; } = true;
        public long SourceChannelId { get; set; }
        public List<long> TargetChannelIds { get; set; } = new List<long>();
        public MessageEditOptions EditOptions { get; set; } = new MessageEditOptions();
        public MessageFilterOptions FilterOptions { get; set; } = new MessageFilterOptions();
    }

    public class MessageEditOptions
    {
        public string? PrependText { get; set; }
        public string? AppendText { get; set; }
        public List<TextReplacementRule>? TextReplacements { get; set; }
        public bool RemoveSourceForwardHeader { get; set; } = false;
        public bool RemoveLinks { get; set; } = false;
        public bool StripFormatting { get; set; } = false;
        public string? CustomFooter { get; set; }
        public bool DropAuthor { get; set; } = false;
        public bool DropMediaCaptions { get; set; } = false;
        public bool NoForwards { get; set; } = false;
    }

    public class TextReplacementRule
    {
        public string Find { get; set; } = string.Empty;
        public string ReplaceWith { get; set; } = string.Empty;
        public bool IsRegex { get; set; } = false;
        public RegexOptions RegexOptions { get; set; } = RegexOptions.IgnoreCase;
    }

    public class MessageFilterOptions
    {
        public List<string>? AllowedMessageTypes { get; set; }
        public List<string>? AllowedMimeTypes { get; set; }
        public string? ContainsText { get; set; }
        public bool ContainsTextIsRegex { get; set; } = false;
        public RegexOptions ContainsTextRegexOptions { get; set; } = RegexOptions.IgnoreCase;
        public List<long>? AllowedSenderUserIds { get; set; }
        public List<long>? BlockedSenderUserIds { get; set; }
        public bool IgnoreEditedMessages { get; set; } = false;
        public bool IgnoreServiceMessages { get; set; } = true;
        public int? MinMessageLength { get; set; }
        public int? MaxMessageLength { get; set; }
        public List<string>? BlockedTexts { get; set; }
    }
} 