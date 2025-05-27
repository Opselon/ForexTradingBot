// File: Domain\Features\Forwarding\ValueObjects\MessageEditOptions.cs
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Domain.Features.Forwarding.ValueObjects
{
    public class MessageEditOptions
    {
        public string? PrependText { get; private set; }
        public string? AppendText { get; private set; }
        // Changed type from TextReplacementRule to TextReplacement (assuming your TextReplacement.cs name is TextReplacement)
        public IReadOnlyList<TextReplacement>? TextReplacements { get; private set; }
        public bool RemoveSourceForwardHeader { get; private set; }
        public bool RemoveLinks { get; private set; }
        public bool StripFormatting { get; private set; }
        public string? CustomFooter { get; private set; }
        public bool DropAuthor { get; private set; }
        public bool DropMediaCaptions { get; private set; }
        public bool NoForwards { get; private set; }

        private MessageEditOptions() { } // For EF Core and JSON deserialization

        public MessageEditOptions(
            string? prependText,
            string? appendText,
            IReadOnlyList<TextReplacement>? textReplacements, // Changed to TextReplacement
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
            // Ensure lists are not null if passed as null (to avoid NullReferenceException later)
            TextReplacements = textReplacements ?? new List<TextReplacement>();
            RemoveSourceForwardHeader = removeSourceForwardHeader;
            RemoveLinks = removeLinks;
            StripFormatting = stripFormatting;
            CustomFooter = customFooter;
            DropAuthor = dropAuthor;
            DropMediaCaptions = dropMediaCaptions;
            NoForwards = noForwards;
        }

        // Add a builder pattern if you want to update these options in rule
        // (Existing method from prior conversation, just kept here for completeness)
        public MessageEditOptions With(
            string? prependText = null,
            string? appendText = null,
            IReadOnlyList<TextReplacement>? textReplacements = null,
            bool? removeSourceForwardHeader = null,
            bool? removeLinks = null,
            bool? stripFormatting = null,
            string? customFooter = null,
            bool? dropAuthor = null,
            bool? dropMediaCaptions = null,
            bool? noForwards = null)
        {
            return new MessageEditOptions(
                prependText ?? this.PrependText,
                appendText ?? this.AppendText,
                textReplacements ?? this.TextReplacements,
                removeSourceForwardHeader ?? this.RemoveSourceForwardHeader,
                removeLinks ?? this.RemoveLinks,
                stripFormatting ?? this.StripFormatting,
                customFooter ?? this.CustomFooter,
                dropAuthor ?? this.DropAuthor,
                dropMediaCaptions ?? this.DropMediaCaptions,
                noForwards ?? this.NoForwards
            );
        }
    }
}