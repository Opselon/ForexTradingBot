// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;
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
        #endregion

        #region Constructor
        public NotificationDispatchService(
            INewsItemRepository newsItemRepository,
            IUserRepository userRepository,
            INotificationJobScheduler jobScheduler,
            ILogger<NotificationDispatchService> logger,
            IConnectionMultiplexer redisConnection,   // It receives this...
            INotificationRateLimiter rateLimiter)      // and this.
        {
            // Assign all the dependencies you receive.
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter)); // ✅ ASSIGN the rate limiter

            if (redisConnection == null) throw new ArgumentNullException(nameof(redisConnection));
            _redisDb = redisConnection.GetDatabase(); // ✅ ASSIGN the Redis DB instance
        }
        #endregion

        #region INotificationDispatchService Implementation

        private readonly TimeSpan _delayBetweenJobEnqueues = TimeSpan.FromMilliseconds(50); // Configurable

        /// <summary>
        /// Asynchronously dispatches notifications for a specified news item to eligible users.
        /// This method enqueues one job per user with a delay between each enqueue operation
        /// to manage load and respect potential rate limits at the job scheduling level.
        /// </summary>
        /// <param name="newsItemId">The unique identifier of the news item to dispatch notifications for.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <summary>
        /// Dispatches notifications using a hyper-resilient, memory-efficient streaming algorithm
        /// equipped with a multi-layer "Hang Shield" to ensure application stability.
        /// </summary>
        public Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            #region --- Hang Shield & Algorithm Configuration ---
            var TotalDispatchTimeout = TimeSpan.FromHours(3);
            var PerEnqueueTimeout = TimeSpan.FromSeconds(10);
            const int DispatchChunkSize = 100;
            const double TargetJobsPerSecond = 25.0;
            const double JitterFactor = 0.2;
            const int CircuitBreakerThreshold = 15;
            const int ProgressLoggingInterval = 5000;
            #endregion

            return Task.Run(async () =>
            {
                using var dispatchTimeoutCts = new CancellationTokenSource(TotalDispatchTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dispatchTimeoutCts.Token);
                var masterToken = linkedCts.Token;

                try
                {
                    NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, masterToken);
                    if (newsItem == null)
                    {
                        _logger.LogWarning("News item with ID {Id} not found. Cannot dispatch.", newsItemId);
                        return;
                    }

                    using (_logger.BeginScope(new Dictionary<string, object?> { ["NewsItemId"] = newsItem.Id }))
                    {
                        _logger.LogInformation("Initiating CACHE-FIRST dispatch orchestration...");
                        _logger.LogInformation("Fetching all eligible users from the database...");
                        IEnumerable<User> targetUsersStream = await _userRepository.GetUsersForNewsNotificationAsync(
                            newsItem.AssociatedSignalCategoryId, newsItem.IsVipOnly, masterToken);

                        var validTelegramIds = targetUsersStream?
                            .Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null)
                            .Where(id => id.HasValue)
                            .Select(id => id.Value)
                            .ToList() ?? [];

                        if (!validTelegramIds.Any())
                        {
                            _logger.LogWarning("No eligible users found for this dispatch. Aborting.");
                            return;
                        }
                        _logger.LogInformation("Found {UserCount} eligible users.", validTelegramIds.Count);

                        var userListCacheKey = $"dispatch:users:{newsItemId}";
                        var serializedUserIds = JsonSerializer.Serialize(validTelegramIds);
                        bool cacheSet = await _redisDb.StringSetAsync(userListCacheKey, serializedUserIds, TimeSpan.FromHours(24));
                        if (!cacheSet)
                        {
                            _logger.LogCritical("Failed to set user list in Redis cache. Aborting dispatch.");
                            return;
                        }
                        _logger.LogInformation("Successfully cached {UserCount} user IDs in Redis. Key: {CacheKey}", validTelegramIds.Count, userListCacheKey);

                        int totalUsersEnqueued = 0;
                        int processedInChunk = 0;
                        int consecutiveFailures = 0;
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                        for (int i = 0; i < validTelegramIds.Count; i++)
                        {
                            masterToken.ThrowIfCancellationRequested();
                            long userId = validTelegramIds[i];
                            int userIndex = i;

                            if (await _rateLimiter.IsUserOverLimitAsync(userId, 15, TimeSpan.FromHours(1)))
                            {
                                _logger.LogTrace("User {UserId} is over rate limit. Skipping job enqueue.", userId);
                                continue;
                            }

                            try
                            {
                                await Task.Run(() =>
                                {
                                    // --- ✅ FIX 4: ENSURE THIS METHOD EXISTS ON THE INTERFACE ---
                                    // We will define this method in the next step.
                                    _jobScheduler.Enqueue<INotificationSendingService>(
                                        service => service.ProcessNotificationFromCacheAsync(
                                            newsItemId,
                                            userListCacheKey,
                                            userIndex
                                        )
                                    );
                                }, new CancellationTokenSource(PerEnqueueTimeout).Token);
                                totalUsersEnqueued++;
                                consecutiveFailures = 0;
                            }
                            catch (Exception ex)
                            {
                                consecutiveFailures++;
                                _logger.LogError(ex, "Failed to enqueue job for user index {Index}. Consecutive failures: {FailureCount}", userIndex, consecutiveFailures);
                            }

                            if (consecutiveFailures >= CircuitBreakerThreshold)
                            {
                                _logger.LogCritical("CIRCUIT BREAKER TRIPPED. Aborting dispatch.", consecutiveFailures);
                                return;
                            }

                            processedInChunk++;
                            if (processedInChunk >= DispatchChunkSize)
                            {
                                await Task.Yield();
                                stopwatch.Stop();
                                var delayNeeded = TimeSpan.FromSeconds(DispatchChunkSize / TargetJobsPerSecond) - stopwatch.Elapsed;
                                if (delayNeeded > TimeSpan.Zero)
                                {
                                    var jitter = TimeSpan.FromMilliseconds(delayNeeded.TotalMilliseconds * JitterFactor * (Random.Shared.NextDouble() - 0.5) * 2.0);
                                    await Task.Delay(delayNeeded + jitter, masterToken);
                                }
                                processedInChunk = 0;
                                stopwatch.Restart();
                            }
                            if (totalUsersEnqueued > 0 && totalUsersEnqueued % ProgressLoggingInterval == 0)
                            {
                                _logger.LogInformation("Dispatch progress: {EnqueuedCount}/{TotalCount} jobs created.", totalUsersEnqueued, validTelegramIds.Count);
                            }
                        }
                        _logger.LogInformation("Orchestration complete. Enqueued {TotalEnqueued} jobs for dispatch.", totalUsersEnqueued);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (dispatchTimeoutCts.IsCancellationRequested) _logger.LogCritical("DISPATCH ABORTED due to master timeout.");
                    else _logger.LogWarning("Dispatch was cancelled externally.");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "An unhandled exception occurred during dispatch orchestration.");
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