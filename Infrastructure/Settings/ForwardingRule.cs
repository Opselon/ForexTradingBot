using System.Collections.Generic;

namespace Infrastructure.Settings
{
    public class ForwardingRule
    {
        public string RuleName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public long SourceChannelId { get; set; }
        public List<long> TargetChannelIds { get; set; } = new();
        public MessageEditOptions EditOptions { get; set; } = new MessageEditOptions();
        public MessageFilterOptions FilterOptions { get; set; } = new MessageFilterOptions();
    }

    public class MessageEditOptions
    {
        public string? PrependText { get; set; }
        public string? AppendText { get; set; }
        public string? CustomFooter { get; set; }
        public bool RemoveSourceForwardHeader { get; set; }
        public bool RemoveLinks { get; set; }
        public bool StripFormatting { get; set; }
        public List<TextReplacementRule>? TextReplacements { get; set; }
    }

    public class TextReplacementRule
    {
        public string Find { get; set; } = string.Empty;
        public string? ReplaceWith { get; set; }
        public bool IsRegex { get; set; }
        public RegexOptions RegexOptions { get; set; } = RegexOptions.None;
    }

    public class MessageFilterOptions
    {
        public List<string> AllowedMessageTypes { get; set; } = new();
        public List<long> AllowedSenderIds { get; set; } = new();
        public bool IgnoreEditedMessages { get; set; }
        public bool IgnoreServiceMessages { get; set; }
    }
} 