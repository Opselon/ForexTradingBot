using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Domain.Features.Forwarding.ValueObjects
{
    public class MessageEditOptions
    {
        public string? PrependText { get; private set; }
        public string? AppendText { get; private set; }
        public IReadOnlyList<TextReplacementRule>? TextReplacements { get; private set; }
        public bool RemoveSourceForwardHeader { get; private set; }
        public bool RemoveLinks { get; private set; }
        public bool StripFormatting { get; private set; }
        public string? CustomFooter { get; private set; }
        public bool DropAuthor { get; private set; }
        public bool DropMediaCaptions { get; private set; }
        public bool NoForwards { get; private set; }

        private MessageEditOptions() { } // For EF Core

        public MessageEditOptions(
            string? prependText,
            string? appendText,
            IReadOnlyList<TextReplacementRule>? textReplacements,
            bool removeSourceForwardHeader,
            bool removeLinks,
            bool stripFormatting,
            string? customFooter,
            bool dropAuthor,
            bool dropMediaCaptions,
            bool noForwards)
        {
            PrependText = prependText;
            AppendText = appendText;
            TextReplacements = textReplacements;
            RemoveSourceForwardHeader = removeSourceForwardHeader;
            RemoveLinks = removeLinks;
            StripFormatting = stripFormatting;
            CustomFooter = customFooter;
            DropAuthor = dropAuthor;
            DropMediaCaptions = dropMediaCaptions;
            NoForwards = noForwards;
        }
    }
} 