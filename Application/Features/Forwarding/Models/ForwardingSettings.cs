// File: src/Infrastructure/Settings/ForwardingSettings.cs
using System.Collections.Generic;
using System.Text.RegularExpressions; // Required for RegexOptions

namespace Infrastructure.Settings
{
    public class ForwardingRule
    {
        public string RuleName { get; set; } = "DefaultRule";
        public bool IsEnabled { get; set; } = true;
        public long SourceChannelId { get; set; } // Normalized ID (e.g., -1001234567890 for channels)
        public List<long> TargetChannelIds { get; set; } = new List<long>();
        public MessageEditOptions EditOptions { get; set; } = new MessageEditOptions();
        public MessageFilterOptions FilterOptions { get; set; } = new MessageFilterOptions();
    }

    public class MessageEditOptions
    {
        public string? PrependText { get; set; }
        public string? AppendText { get; set; }
        public List<TextReplacementRule>? TextReplacements { get; set; }
        public bool RemoveSourceForwardHeader { get; set; } = false; // WTelegramClient's ForwardMessages has `drop_author`
        public bool RemoveLinks { get; set; } // If true, will attempt to remove link entities and text URLs
        public bool StripFormatting { get; set; } = false; // If true, will attempt to remove entities and send plain text
        public string? CustomFooter { get; set; } // Alternative to AppendText, might be handled similarly
                                                  // Add more as needed, e.g., for watermarking images (complex)
    }

    public class TextReplacementRule
    {
        public string Find { get; set; } = string.Empty;
        public string ReplaceWith { get; set; } = string.Empty;
        public bool IsRegex { get; set; } = false;
        public RegexOptions RegexOptions { get; set; } = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
    }

    public class MessageFilterOptions
    {
        public List<string>? AllowedMessageTypes { get; set; } // e.g., "Text", "Photo", "Video", "Document", "Audio", "Voice", "Animation", "Sticker"
        public List<string>? AllowedMimeTypes { get; set; } // For "Document" type, e.g., "application/pdf"
        public string? ContainsText { get; set; }
        public bool ContainsTextIsRegex { get; set; } = false;
        public RegexOptions ContainsTextRegexOptions { get; set; } = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        public List<long>? AllowedSenderUserIds { get; set; } // User IDs (positive)
        public List<long>? BlockedSenderUserIds { get; set; } // User IDs (positive)
        public bool IgnoreEditedMessages { get; set; } = false;
        public int? MinMessageLength { get; set; } // Filter by minimum text length
        public int? MaxMessageLength { get; set; } // Filter by maximum text length
    }
}