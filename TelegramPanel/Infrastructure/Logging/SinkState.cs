// --- File: Infrastructure/Logging/SinkState.cs ---

using Serilog.Events;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Logging
{
    /// <summary>
    /// Manages the state for the God Mode sink, primarily for intelligent error deduplication and throttling.
    /// This prevents alert storms by tracking the frequency of unique errors.
    /// </summary>
    public class SinkState
    {
        private readonly ConcurrentDictionary<string, ErrorOccurrence> _errorCache = new();
        private readonly TimeSpan _throttlingPeriod = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Represents a unique error occurrence for tracking.
        /// </summary>
        private class ErrorOccurrence
        {
            public int Count { get; set; } = 1;
            public DateTime FirstSeen { get; } = DateTime.UtcNow;
            public DateTime LastNotification { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Determines if a given log event should be throttled (i.e., not sent as a new notification).
        /// </summary>
        /// <param name="logEvent">The log event to check.</param>
        /// <param name="occurrenceCount">The number of times this specific error has occurred.</param>
        /// <returns>True if the notification should be suppressed; otherwise, false.</returns>
        public bool ShouldThrottle(LogEvent logEvent, out int occurrenceCount)
        {
            var errorSignature = CreateErrorSignature(logEvent);
            occurrenceCount = 1;

            if (_errorCache.TryGetValue(errorSignature, out var occurrence))
            {
                occurrence.Count++;
                occurrenceCount = occurrence.Count;

                if (DateTime.UtcNow - occurrence.LastNotification < _throttlingPeriod)
                {
                    return true; // Suppress the notification, it's too soon.
                }

                // Throttling period has passed, so we will send a summary notification.
                occurrence.LastNotification = DateTime.UtcNow;
                return false;
            }

            // This is a new, unique error.
            _errorCache.TryAdd(errorSignature, new ErrorOccurrence());
            return false;
        }

        /// <summary>
        /// Creates a unique, stable hash signature for a log event based on its core properties.
        /// </summary>
        private string CreateErrorSignature(LogEvent logEvent)
        {
            var sb = new StringBuilder();
            sb.Append(logEvent.Exception?.GetType().Name);
            sb.Append(logEvent.MessageTemplate.Text);

            if (logEvent.Properties.TryGetValue("Caller", out var caller))
            {
                sb.Append(caller.ToString("l", null));
            }

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hashBytes);
        }
    }
}