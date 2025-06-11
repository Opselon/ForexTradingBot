using System.Globalization; // For CultureInfo, TextInfo
using System.Security.Cryptography; // For SHA256
using System.Text; // For StringBuilder, Encoding
using System.Text.RegularExpressions; // For Regex

namespace Shared.Extensions
{
    /// <summary>
    /// Provides a set of static extension methods for <see cref="string"/> objects,
    /// offering common utility functions for string manipulation, validation, and conversion.
    /// These extensions aim to improve code readability and reusability.
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// Determines whether a specified string is null, empty, or consists only of white-space characters.
        /// This is a convenience wrapper around <see cref="string.IsNullOrWhiteSpace(string?)"/>.
        /// </summary>
        /// <param name="value">The string to test.</param>
        /// <returns>
        /// <see langword="true"/> if the <paramref name="value"/> parameter is null,
        /// an empty string (""), or if it consists only of white-space characters; otherwise, <see langword="false"/>.
        /// </returns>
        public static bool IsNullOrWhiteSpace(this string? value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Determines whether a specified string is null or an empty string.
        /// This is a convenience wrapper around <see cref="string.IsNullOrEmpty(string?)"/>.
        /// </summary>
        /// <param name="value">The string to test.</param>
        /// <returns>
        /// <see langword="true"/> if the <paramref name="value"/> parameter is null or an empty string ("");
        /// otherwise, <see langword="false"/>.
        /// </returns>
        public static bool IsNullOrEmpty(this string? value)
        {
            return string.IsNullOrEmpty(value);
        }

        /// <summary>
        /// Converts the specified string representation of the name or numeric value of one or more enumerated constants
        /// to an equivalent enumerated object of type <typeparamref name="T"/>.
        /// Throws an <see cref="ArgumentException"/> if the value is null, empty, or cannot be parsed.
        /// </summary>
        /// <typeparam name="T">The enumeration type to which to convert <paramref name="value"/>.</typeparam>
        /// <param name="value">A string containing the name or value to convert.</param>
        /// <param name="ignoreCase">A boolean indicating whether the parsing should be case-insensitive.</param>
        /// <returns>An object of type <typeparamref name="T"/> whose value is represented by <paramref name="value"/>.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="value"/> is null or whitespace, or <paramref name="value"/>
        /// does not represent a valid constant of <typeparamref name="T"/>,
        /// or <typeparamref name="T"/> is not an enumeration type.
        /// </exception>
        /// <exception cref="OverflowException"><paramref name="value"/> is outside the range of the underlying type of <typeparamref name="T"/>.</exception>
        public static T ToEnum<T>(this string value, bool ignoreCase = true) where T : struct, Enum
        {
            return value.IsNullOrWhiteSpace()
                ? throw new ArgumentException("Value cannot be null or whitespace for enum conversion.", nameof(value))
                : (T)Enum.Parse(typeof(T), value, ignoreCase);
        }

        /// <summary>
        /// Attempts to convert the specified string representation of the name or numeric value of one or more enumerated constants
        /// to an equivalent enumerated object of type <typeparamref name="T"/>.
        /// This method returns a boolean indicating success or failure and does not throw an exception on invalid input.
        /// </summary>
        /// <typeparam name="T">The enumeration type to which to convert <paramref name="value"/>.</typeparam>
        /// <param name="value">A string containing the name or value to convert.</param>
        /// <param name="enumValue">When this method returns, contains an object of type <typeparamref name="T"/>
        /// whose value is represented by <paramref name="value"/>, or the default value of <typeparamref name="T"/>
        /// if the parse operation fails.</param>
        /// <param name="ignoreCase">A boolean indicating whether the parsing should be case-insensitive.</param>
        /// <returns>
        /// <see langword="true"/> if the <paramref name="value"/> parameter was converted successfully;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        public static bool TryToEnum<T>(this string? value, out T enumValue, bool ignoreCase = true) where T : struct, Enum
        {
            // Use IsNullOrWhiteSpace here for robustness before parsing
            if (value.IsNullOrWhiteSpace())
            {
                enumValue = default; // Assign default value as per TryParse contract
                return false;
            }
            return Enum.TryParse(value, ignoreCase, out enumValue);
        }

        /// <summary>
        /// Computes the SHA256 hash of the input string.
        /// The hash is returned as a lowercase hexadecimal string.
        /// Returns an empty string if the input value is null or empty.
        /// </summary>
        /// <param name="value">The input string to hash.</param>
        /// <returns>A 64-character hexadecimal string representing the SHA256 hash of the input, or an empty string if input is null/empty.</returns>
        public static string ToSha256(this string? value)
        {
            if (value.IsNullOrEmpty())
            {
                return string.Empty;
            }

            using var sha256 = SHA256.Create();
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                // "x2" formats the byte as a two-digit hexadecimal number
                _ = builder.Append(bytes[i].ToString("x2"));
            }
            return builder.ToString();
        }

        /// <summary>
        /// Converts the specified string to Title Case using the casing rules of the current culture.
        /// This means the first letter of each word is capitalized, and the rest are lowercase.
        /// Handles null or whitespace strings gracefully by returning them unchanged.
        /// </summary>
        /// <param name="value">The string to convert to Title Case.</param>
        /// <returns>The specified string in Title Case, or the original string if it was null or whitespace.</returns>
        public static string ToTitleCase(this string? value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return value;
            }
            // It's often recommended to convert to lower first for consistency across cultures
            // and then apply TitleCase. Use InvariantCulture for lower if the input source is invariant.
            // CurrentCulture is fine if this is for display to the user.
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture));
        }

        /// <summary>
        /// Removes all HTML tags from the input string, returning only the plain text content.
        /// This is a simple implementation using regular expressions and may not handle all complex
        /// HTML structures or malformed HTML correctly. For robust HTML parsing,
        /// consider using libraries like HtmlAgilityPack or AngleSharp.
        /// </summary>
        /// <param name="value">The string from which to strip HTML tags.</param>
        /// <returns>The input string with HTML tags removed, or an empty string if the input was null or empty.</returns>
        public static string StripHtml(this string? value)
        {
            if (value.IsNullOrEmpty())
            {
                return string.Empty;
            }
            // This regex is generally safe for stripping basic tags but can fail for complex/malformed HTML
            return Regex.Replace(value, "<.*?>", string.Empty);
        }

        /// <summary>
        /// Truncates the input string to a specified maximum length.
        /// If the original string's length exceeds `maxLength`, it is shortened and
        /// an optional truncation suffix (e.g., "...") is appended.
        /// Handles null or empty strings gracefully.
        /// </summary>
        /// <param name="value">The string to truncate.</param>
        /// <param name="maxLength">The maximum allowed length of the string, including the truncation suffix.</param>
        /// <param name="truncationSuffix">The string to append if truncation occurs (default is "...").</param>
        /// <returns>
        /// The truncated string. If the original string is shorter than or equal to `maxLength`,
        /// the original string is returned. If `maxLength` is less than or equal to the
        /// length of `truncationSuffix`, only the suffix is returned.
        /// </returns>
        public static string Truncate(this string? value, int maxLength, string truncationSuffix = "...")
        {
            if (value.IsNullOrEmpty() || value.Length <= maxLength)
            {
                return value ?? string.Empty; // Return empty string for null input if not truncated
            }

            if (maxLength <= truncationSuffix.Length)
            {
                return truncationSuffix;
            }

            // Ensure we don't try to take a substring with negative length
            int substringLength = maxLength - truncationSuffix.Length;
            if (substringLength < 0)
            {
                substringLength = 0; // Defensive check
            }

            return value.Substring(0, substringLength) + truncationSuffix;
        }
    }
}