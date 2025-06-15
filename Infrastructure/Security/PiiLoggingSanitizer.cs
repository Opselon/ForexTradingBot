using Application.Common.Interfaces; // Using the established interface path
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Infrastructure.Security
{
    /// <summary>
    /// A robust, reusable service for sanitizing sensitive data before it is logged.
    /// This is the concrete implementation of the ILoggingSanitizer interface.
    /// It must be registered as a Singleton in the DI container.
    /// </summary>
    public class PiiLoggingSanitizer : ILoggingSanitizer
    {
        private readonly ILogger<PiiLoggingSanitizer> _logger;

        // Using a static, compiled list of rules is highly performant and extensible.
        private static readonly IReadOnlyList<(Regex Pattern, string Replacement)> RedactionRules = new List<(Regex, string)>
        {
            // Rule for email addresses
            (new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase), "[REDACTED_EMAIL]"),
            
            // Rule for common US phone number formats
            (new Regex(@"\(?\b\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled), "[REDACTED_PHONE]"),
            
            // FUTURE: Add more rules here for other PII types like credit cards, etc.
        };

        private const int MaxLogLength = 250; // Max length for any logged string fragment

        public PiiLoggingSanitizer(ILogger<PiiLoggingSanitizer> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Sanitizes a string to prevent PII/sensitive data exposure in logs by redacting
        /// known patterns and truncating the result. This method is designed to be fail-safe.
        /// </summary>
        /// <param name="input">The potentially sensitive string to sanitize.</param>
        /// <returns>A sanitized string that is safe for logging.</returns>
        public string Sanitize(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "N/A"; // Use a consistent placeholder for null/empty inputs.
            }

            try
            {
                // 1. Truncate first to limit the amount of data being processed.
                string sanitized = input.Length > MaxLogLength
                    ? input.Substring(0, MaxLogLength) + "..."
                    : input;

                // 2. Apply all redaction rules.
                foreach (var rule in RedactionRules)
                {
                    sanitized = rule.Pattern.Replace(sanitized, rule.Replacement);
                }

                // 3. Final cleanup for log-forging characters (CRLF injection).
                return sanitized.Replace(Environment.NewLine, " ").Replace("\r", " ").Replace("\n", " ");
            }
            catch (Exception ex)
            {
                // FAIL-SAFE: If sanitization logic itself fails, log the specific error
                // but return a generic, hardcoded placeholder to absolutely prevent
                // leaking the original sensitive data.
                _logger.LogError(ex, "An unexpected error occurred during log sanitization. Input is being fully redacted.");
                return "[SENSITIVE_DATA_SANITIZATION_FAILED]";
            }
        }
    }
}