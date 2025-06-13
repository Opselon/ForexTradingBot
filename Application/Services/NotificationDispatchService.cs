// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;

// using Shared.Extensions; // Assuming TruncateWithEllipsis is a local method
using System.Text;
// using System.Threading.RateLimiting; // Not needed here

// --- ✅ FIX 1: ADD MISSING USINGS ---
using System.Text.Json;
#endregion

namespace Application.Services
{
    public class NotificationDispatchService : INotificationDispatchService
    {
        #region Private Readonly Fields
        private readonly IUserRepository _userRepository;
        private readonly INotificationJobScheduler _jobScheduler;
        private readonly ILogger<NotificationDispatchService> _logger;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly StackExchange.Redis.IDatabase _redisDb;
        private readonly INotificationRateLimiter _rateLimiter;
        private readonly AsyncCircuitBreakerPolicy _redisCircuitBreaker;
        #endregion

        #region Constructor
        public NotificationDispatchService(
             INewsItemRepository newsItemRepository,
             IUserRepository userRepository,
             INotificationJobScheduler jobScheduler,
             ILogger<NotificationDispatchService> logger,
             IConnectionMultiplexer redisConnection,
             INotificationRateLimiter rateLimiter)
        {
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

            if (redisConnection == null) throw new ArgumentNullException(nameof(redisConnection));
            _redisDb = redisConnection.GetDatabase();

            // ✅✅ NEW: Initialize the Circuit Breaker policy ✅✅
            // If 3 consecutive Redis operations fail, the circuit will "break" (stop trying) for 1 minute.
            _redisCircuitBreaker = Policy
                .Handle<RedisException>() // It will trigger on any Redis-specific exception
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromMinutes(1),
                    onBreak: (exception, timespan) =>
                    {
                        _logger.LogCritical(exception, "Redis Circuit Breaker opened for {BreakDuration}. All dispatch operations will fail fast until the circuit resets.", timespan);
                    },
                    onReset: () => _logger.LogInformation("Redis Circuit Breaker has been reset. Resuming normal dispatch operations."),
                    onHalfOpen: () => _logger.LogWarning("Redis Circuit Breaker is now half-open. The next dispatch will test the connection.")
                );
        }
        #endregion

        #region INotificationDispatchService Implementation

        private readonly TimeSpan _delayBetweenJobEnqueues = TimeSpan.FromMilliseconds(50); // Configurable

        /// <summary>
        /// Fetches users, caches their IDs in Redis, and enqueues a lightweight job for each user.
        /// This method does NOT build the final message content.
        /// </summary>
        /// <summary>
        /// Fetches users, caches their IDs in Redis, and enqueues a lightweight job for each user.
        /// This method does NOT build the final message content.
        /// </summary>
        /// <summary>
        /// Orchestrates a large-scale notification dispatch. It fetches all eligible users,
        //  caches their IDs in Redis using a Circuit Breaker for resilience, and then enqueues
        /// a lightweight job for each user.
        /// </summary>
        public Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            var delayBetweenJobs = TimeSpan.FromMilliseconds(40);

            return Task.Run(async () =>
            {
                // ✅✅ NEW: Check the circuit state BEFORE doing any work ✅✅
                if (_redisCircuitBreaker.CircuitState == CircuitState.Open)
                {
                    _logger.LogWarning("Dispatch for NewsItem {NewsItemId} skipped because the Redis circuit breaker is open.", newsItemId);
                    return; // Fail fast without hitting the database
                }

                try
                {
                    NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
                    if (newsItem == null)
                    {
                        _logger.LogWarning("NewsItem {Id} not found. Cannot dispatch.", newsItemId);
                        return;
                    }
                    _logger.LogInformation("Dispatching for NewsItem: {Title}", newsItem.Title);

                    IEnumerable<User> targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                        newsItem.AssociatedSignalCategoryId, newsItem.IsVipOnly, cancellationToken);

                    var validTelegramIds = targetUsers
                        .Select(u => long.TryParse(u.TelegramId, out var id) ? (long?)id : null)
                        .Where(id => id.HasValue).Select(id => id.Value).ToList();

                    if (!validTelegramIds.Any())
                    {
                        _logger.LogInformation("No eligible users found for this dispatch.");
                        return;
                    }

                    // ✅✅ NEW: Execute the Redis operation within the Circuit Breaker policy ✅✅
                    var userListCacheKey = $"dispatch:users:{newsItemId}";
                    await _redisCircuitBreaker.ExecuteAsync(async () =>
                    {
                        var serializedUserIds = JsonSerializer.Serialize(validTelegramIds);
                        await _redisDb.StringSetAsync(userListCacheKey, serializedUserIds, TimeSpan.FromHours(24));
                    });

                    _logger.LogInformation("Cached {Count} user IDs to Redis key: {Key}", validTelegramIds.Count, userListCacheKey);

                    // --- Job Enqueueing Loop (largely unchanged) ---
                    int enqueuedCount = 0;
                    for (int i = 0; i < validTelegramIds.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long userId = validTelegramIds[i];

                        if (await _rateLimiter.IsUserOverLimitAsync(userId, 15, TimeSpan.FromHours(1)))
                        {
                            _logger.LogTrace("User {UserId} is over rate limit. Skipping job enqueue.", userId);
                            continue;
                        }

                        _jobScheduler.Enqueue<INotificationSendingService>(
                            service => service.ProcessNotificationFromCacheAsync(newsItemId, userListCacheKey, i));
                        enqueuedCount++;

                        if (i < validTelegramIds.Count - 1)
                        {
                            await Task.Delay(delayBetweenJobs, cancellationToken);
                        }
                    }
                    _logger.LogInformation("Dispatch orchestration complete. {Count} jobs enqueued.", enqueuedCount);
                }
                catch (BrokenCircuitException) // ✅ NEW: Specific catch
                {
                    _logger.LogWarning("Dispatch for NewsItem {NewsItemId} failed because the Redis circuit is open.", newsItemId);
                }
                catch (RedisException redisEx) // ✅ NEW: Specific catch
                {
                    _logger.LogError(redisEx, "A Redis error occurred during dispatch orchestration for NewsItem {NewsItemId}. The circuit breaker may have tripped.", newsItemId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Dispatch orchestration was cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "A critical, unhandled error occurred during dispatch orchestration for NewsItem {NewsItemId}.", newsItemId);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Builds the main text content for a news notification.
        /// </summary>
        /// <param name="newsItem">The news item to generate text for.</param>
        /// <returns>Formatted string for the notification message.</returns>
        private string BuildMessageText(NewsItem newsItem)
        {
            if (newsItem == null)
            {
                throw new ArgumentNullException(nameof(newsItem));
            }

            var messageTextBuilder = new StringBuilder();

            string title = EscapeTextForTelegramMarkup(newsItem.Title?.Trim() ?? "Untitled News");
            string sourceName = EscapeTextForTelegramMarkup(newsItem.SourceName?.Trim() ?? "Unknown Source");
            string summary = EscapeTextForTelegramMarkup(TruncateWithEllipsis(newsItem.Summary, 250)?.Trim() ?? string.Empty);
            string? link = newsItem.Link?.Trim();

            _ = messageTextBuilder.AppendLine($"*{title}*");
            _ = messageTextBuilder.AppendLine($"_📰 Source: {sourceName}_");

            if (!string.IsNullOrWhiteSpace(summary))
            {
                _ = messageTextBuilder.Append($"\n{summary}");
            }

            if (!string.IsNullOrWhiteSpace(link))
            {
                if (Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    string escapedLink = link.Replace("(", "\\(").Replace(")", "\\)");
                    _ = messageTextBuilder.Append($"\n\n[🔗 Read Full Article]({escapedLink})");
                }
                else
                {
                    _logger.LogWarning("Invalid URL format for news item link. NewsItemID: {NewsItemId}, Link: {Link}", newsItem.Id, link);
                }
            }
            return messageTextBuilder.ToString().Trim();
        }


        /// <summary>
        /// Builds a list of notification buttons for a news item.
        /// </summary>
        private List<NotificationButton> BuildNotificationButtons(NewsItem newsItem) // Correct return type
        {
            if (newsItem == null)
            {
                throw new ArgumentNullException(nameof(newsItem));
            }

            var buttons = new List<NotificationButton>();
            if (!string.IsNullOrWhiteSpace(newsItem.Link) && Uri.TryCreate(newsItem.Link, UriKind.Absolute, out _))
            {
                buttons.Add(new NotificationButton { Text = "Read More", CallbackDataOrUrl = newsItem.Link, IsUrl = true });
            }
            return buttons;
        }

        /// <summary>
        /// Truncates text to a maximum length, appending ellipsis if truncated.
        /// </summary>
        private string? TruncateWithEllipsis(string? text, int maxLength)
        {
            return string.IsNullOrWhiteSpace(text) ? text : text.Length <= maxLength ? text : text.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Escapes characters in plain text that have special meaning in Telegram Markdown (V1/relaxed V2 compatible).
        /// This method targets minimal necessary escaping to produce clean output without extraneous backslashes
        /// on common punctuation like periods, hyphens, or slashes, as seen in the user's desired output format.
        /// </summary>
        private string EscapeTextForTelegramMarkup(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length + 10);
            foreach (char c in text)
            {
                switch (c)
                {
                    // Escape only critical Markdown characters that would otherwise break formatting.
                    // Characters like '.', '-', '/', '!' etc., are typically NOT escaped in desired output.
                    case '_': // Italic (Telegram V1 uses this)
                    case '*': // Bold (Telegram V1/V2 accepts single '*')
                    case '[': // Link start
                    case ']': // Link end
                    case '(': // Parenthesis (important if literal parentheses appear in text that might be part of URL syntax)
                    case ')': // Parenthesis
                    case '~': // Strikethrough (primarily MarkdownV2, but escaping doesn't hurt)
                    case '`': // Code/Pre (primarily MarkdownV2, but escaping doesn't hurt)
                    case '>': // Blockquote (primarily MarkdownV2, but escaping doesn't hurt)
                        _ = sb.Append('\\');
                        break;
                }
                _ = sb.Append(c);
            }
            return sb.ToString();
        }

        #endregion
    }
}