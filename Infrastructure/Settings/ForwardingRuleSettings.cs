// File: src/Infrastructure/Settings/ForwardingRuleSettings.cs
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Infrastructure.Settings
{
    public class ForwardingRule
    {
        public string RuleName { get; set; } = "DefaultRule"; // نامی برای شناسایی قانون
        public bool IsEnabled { get; set; } = true;
        public long SourceChannelId { get; set; } // شناسه عددی کانال مبدأ (مثلاً -1001234567890)
        public List<long> TargetChannelIds { get; set; } = new List<long>(); // لیست شناسه‌های کانال‌های مقصد
        public MessageEditOptions EditOptions { get; set; } = new MessageEditOptions();
        public MessageFilterOptions FilterOptions { get; set; } = new MessageFilterOptions();
    }

    public class MessageEditOptions
    {
        public string? PrependText { get; set; }
        public string? AppendText { get; set; }
        public List<TextReplacementRule>? TextReplacements { get; set; }
        public bool RemoveSourceForwardHeader { get; set; } = true; // حذف "Forwarded from"
        public bool RemoveLinks { get; set; } // ساده یا با Regex
        public string? CustomFooter { get; set; }
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
        public List<string>? AllowedMessageTypes { get; set; } // "Text", "Photo", "Video", "Document", "Audio", "Voice"
        public List<string>? AllowedMimeTypes { get; set; } // برای Document
        public string? ContainsText { get; set; } // فیلتر بر اساس متن (می‌تواند Regex باشد)
        public bool ContainsTextIsRegex { get; set; } = false;
        public List<long>? AllowedSenderUserIds { get; set; } // فقط پیام‌های این کاربران فوروارد شود (در گروه/کانال مبدأ)
        public List<long>? BlockedSenderUserIds { get; set; }
    }
}