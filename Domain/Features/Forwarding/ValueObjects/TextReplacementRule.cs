using System.Text.RegularExpressions;

namespace Domain.Features.Forwarding.ValueObjects
{
    public class TextReplacementRule
    {
        public string Find { get; private set; }
        public string ReplaceWith { get; private set; }
        public bool IsRegex { get; private set; }
        public RegexOptions RegexOptions { get; private set; }

        private TextReplacementRule() { } // For EF Core

        public TextReplacementRule(
            string find,
            string replaceWith,
            bool isRegex = false,
            RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Find = find;
            ReplaceWith = replaceWith;
            IsRegex = isRegex;
            RegexOptions = regexOptions;
        }
    }
} 