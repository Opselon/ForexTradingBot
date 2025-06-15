// File: Application/Common/Interfaces/ILoggingSanitizer.cs
namespace Application.Common.Interfaces;

/// <summary>
/// Defines a service for sanitizing potentially sensitive data before it is logged.
/// </summary>
public interface ILoggingSanitizer
{
    /// <summary>
    /// Sanitizes a string to prevent PII/sensitive data exposure in logs.
    /// </summary>
    /// <param name="input">The potentially sensitive string to sanitize.</param>
    /// <returns>A sanitized string that is safe for logging.</returns>
    string Sanitize(string? input);
}