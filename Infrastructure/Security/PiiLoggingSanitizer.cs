// This logic will reside in the new Infrastructure/Security/PiiLoggingSanitizer.cs file
using Application.Common.Interfaces;
using System.Text.RegularExpressions;

public class PiiLoggingSanitizer : ILoggingSanitizer
{
    // ... constructor and ILogger field ...

    private static readonly IReadOnlyList<(Regex Pattern, string Replacement)> RedactionRules = new List<(Regex, string)>
    {
        // Rule for emails
        (new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase), "[REDACTED_EMAIL]"),
        // Rule for common US phone number formats
        (new Regex(@"\(?\b\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled), "[REDACTED_PHONE]")
        // Add more rules here for things like credit card numbers, SSNs, etc. if applicable
    };

    private const int MaxLogLength = 250;

    public string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "N/A"; // Return a non-empty, non-null placeholder
        }

        try
        {
            string sanitized = input.Length > MaxLogLength ? input.Substring(0, MaxLogLength) + "..." : input;

            foreach (var rule in RedactionRules)
            {
                sanitized = rule.Pattern.Replace(sanitized, rule.Replacement);
            }

            // Final pass for log forging characters
            return sanitized.Replace(Environment.NewLine, " ").Replace("\r", " ").Replace("\n", " ");
        }
        catch (Exception ex)
        {
            // Failsafe: if sanitization fails, do not leak the original data.
            _logger.LogError(ex, "An error occurred during log sanitization. Input is being fully redacted.");
            return "[SENSITIVE_DATA_SANITIZATION_FAILED]";
        }
    }
}