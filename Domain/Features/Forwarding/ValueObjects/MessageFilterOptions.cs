using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Domain.Features.Forwarding.ValueObjects
{
    public class MessageFilterOptions
    {
        public IReadOnlyList<string>? AllowedMessageTypes { get; private set; }
        public IReadOnlyList<string>? AllowedMimeTypes { get; private set; }
        public string? ContainsText { get; private set; }
        public bool ContainsTextIsRegex { get; private set; }
        public RegexOptions ContainsTextRegexOptions { get; private set; }
        public IReadOnlyList<long>? AllowedSenderUserIds { get; private set; }
        public IReadOnlyList<long>? BlockedSenderUserIds { get; private set; }
        public bool IgnoreEditedMessages { get; private set; }
        public bool IgnoreServiceMessages { get; private set; }
        public int? MinMessageLength { get; private set; }
        public int? MaxMessageLength { get; private set; }

        private MessageFilterOptions() { } // For EF Core

        public MessageFilterOptions(
            IReadOnlyList<string>? allowedMessageTypes,
            IReadOnlyList<string>? allowedMimeTypes,
            string? containsText,
            bool containsTextIsRegex,
            RegexOptions containsTextRegexOptions,
            IReadOnlyList<long>? allowedSenderUserIds,
            IReadOnlyList<long>? blockedSenderUserIds,
            bool ignoreEditedMessages,
            bool ignoreServiceMessages,
            int? minMessageLength,
            int? maxMessageLength)
        {
            AllowedMessageTypes = allowedMessageTypes;
            AllowedMimeTypes = allowedMimeTypes;
            ContainsText = containsText;
            ContainsTextIsRegex = containsTextIsRegex;
            ContainsTextRegexOptions = containsTextRegexOptions;
            AllowedSenderUserIds = allowedSenderUserIds;
            BlockedSenderUserIds = blockedSenderUserIds;
            IgnoreEditedMessages = ignoreEditedMessages;
            IgnoreServiceMessages = ignoreServiceMessages;
            MinMessageLength = minMessageLength;
            MaxMessageLength = maxMessageLength;
        }
    }
} 