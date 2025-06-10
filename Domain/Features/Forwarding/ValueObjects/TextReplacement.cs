// File: Domain\Features\Forwarding\ValueObjects\TextReplacement.cs
using System.Text.RegularExpressions;

namespace Domain.Features.Forwarding.ValueObjects
{
    // A single rule for text replacement within a message
    public class TextReplacement // <<<< نام کلاس را به TextReplacement تغییر دادم
    {
        public string Find { get; private set; } = null!;
        public string ReplaceWith { get; private set; } = null!;
        public bool IsRegex { get; private set; } 
        public RegexOptions RegexOptions { get; private set; }

        private TextReplacement() { } // For EF Core

        public TextReplacement(
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