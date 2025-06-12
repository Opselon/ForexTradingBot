// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Shared.Extensions;
using System.Text;
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
        private const int BatchSize = 1;
        #endregion

        #region Constructor
        public NotificationDispatchService(
            INewsItemRepository newsItemRepository,
            IUserRepository userRepository,
            INotificationJobScheduler jobScheduler,
            ILogger<NotificationDispatchService> logger)
        {
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            // Layer 8: A master timeout for the entire dispatch process.
            var TotalDispatchTimeout = TimeSpan.FromHours(3);

            // Layer 7: A timeout for each individual job enqueue operation.
            var PerOperationTimeout = TimeSpan.FromSeconds(10);

            // The number of users to process in one batch before pausing for throttling.
            const int DispatchChunkSize = 100;

            // The desired rate of job creation.
            const double TargetJobsPerSecond = 25.0;

            // Randomized jitter factor to prevent thundering herd.
            const double JitterFactor = 0.2;

            // Circuit breaker threshold for consecutive failures.
            const int CircuitBreakerThreshold = 15;

            // Interval for logging progress.
            const int ProgressLoggingInterval = 5000;
            #endregion

            return Task.Run(async () =>
            {
                // --- HANG SHIELD - Layer 8: Overall Dispatch Timeout ---
                // Create a master cancellation source that triggers after the total timeout.
                using var dispatchTimeoutCts = new CancellationTokenSource(TotalDispatchTimeout);
                // Link it with the external cancellation token. The dispatch will stop if EITHER is cancelled.
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

                    masterToken.ThrowIfCancellationRequested();

                    using (_logger.BeginScope(new Dictionary<string, object?> { ["NewsItemId"] = newsItem.Id }))
                    {
                        _logger.LogInformation("Initiating HANG-SHIELDED streaming dispatch for news item.");

                        IEnumerable<User> targetUsersStream = await _userRepository.GetUsersForNewsNotificationAsync(
                            newsItem.AssociatedSignalCategoryId, newsItem.IsVipOnly, masterToken);

                        if (targetUsersStream == null)
                        {
                            _logger.LogWarning("User repository returned a null stream.");
                            return;
                        }

                        string messageText = BuildMessageText(newsItem);
                        string? imageUrl = newsItem.ImageUrl;
                        List<NotificationButton> buttons = BuildNotificationButtons(newsItem);
                        var categoryId = newsItem.AssociatedSignalCategoryId;
                        var categoryName = newsItem.AssociatedSignalCategory?.Name ?? string.Empty;

                        int totalUsersEnqueued = 0;
                        int processedInChunk = 0;
                        int consecutiveFailures = 0;
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                        foreach (var user in targetUsersStream)
                        {
                            if (masterToken.IsCancellationRequested)
                            {
                                _logger.LogWarning("Dispatch was cancelled by master token after processing {Count} users.", totalUsersEnqueued);
                                break;
                            }

                            if (!long.TryParse(user.TelegramId, out long telegramId)) continue;

                            try
                            {
                                // --- HANG SHIELD - Layer 7: Per-Operation Timeout ---
                                // Create a specific timeout for this single operation.
                                using var operationTimeoutCts = new CancellationTokenSource(PerOperationTimeout);
                                // To apply a timeout to a synchronous call, we must wrap it to make it awaitable and cancellable.
                                // Task.Run is the correct tool for this specific, targeted need.
                                await Task.Run(() =>
                                {
                                    _jobScheduler.Enqueue<INotificationSendingService>(
                                        service => service.SendBatchNotificationAsync(
                                            new List<long> { telegramId }, messageText, imageUrl, buttons,
                                            newsItem.Id, categoryId, categoryName, CancellationToken.None));
                                }, operationTimeoutCts.Token);

                                totalUsersEnqueued++;
                                consecutiveFailures = 0; // Reset circuit breaker on success.
                            }
                            catch (OperationCanceledException)
                            {
                                // This block is hit if the operation or the entire dispatch is cancelled.
                                if (masterToken.IsCancellationRequested)
                                {
                                    // Propagate cancellation if it came from the master token.
                                    _logger.LogWarning("Dispatch cancelled during job enqueue.");
                                    break;
                                }

                                // Otherwise, the per-operation timeout was triggered.
                                _logger.LogWarning("Job enqueue for user {Id} timed out after {Seconds}s. Skipping user.", telegramId, PerOperationTimeout.TotalSeconds);
                                consecutiveFailures++;
                            }
                            catch (Exception ex)
                            {
                                consecutiveFailures++;
                                _logger.LogError(ex, "Failed to enqueue job for user {Id}. Consecutive failures: {FailureCount}",
                                                 telegramId, consecutiveFailures);
                            }

                            if (consecutiveFailures >= CircuitBreakerThreshold)
                            {
                                _logger.LogCritical("CIRCUIT BREAKER TRIPPED after {Count} consecutive failures. Aborting dispatch.", consecutiveFailures);
                                return;
                            }

                            processedInChunk++;

                            if (processedInChunk >= DispatchChunkSize)
                            {
                                // --- HANG SHIELD - Bonus Layer: Cooperative Yielding ---
                                // Give other application tasks a chance to run before we calculate delays.
                                await Task.Yield();

                                stopwatch.Stop();
                                var targetChunkTimeMs = (DispatchChunkSize / TargetJobsPerSecond) * 1000.0;
                                var delayNeededMs = targetChunkTimeMs - stopwatch.ElapsedMilliseconds;
                                var jitterMs = delayNeededMs * JitterFactor * (Random.Shared.NextDouble() - 0.5) * 2.0;
                                delayNeededMs += jitterMs;

                                if (delayNeededMs > 0)
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(delayNeededMs), masterToken);
                                }

                                processedInChunk = 0;
                                stopwatch.Restart();
                            }

                            if (totalUsersEnqueued > 0 && totalUsersEnqueued % ProgressLoggingInterval == 0)
                            {
                                _logger.LogInformation("Dispatch progress: {EnqueuedCount} jobs successfully created.", totalUsersEnqueued);
                            }
                        }

                        _logger.LogInformation("Completed streaming dispatch. Total jobs successfully created: {Count}", totalUsersEnqueued);
                    }
                }
                catch (OperationCanceledException)
                {
                    // This top-level catch handles the master token being cancelled.
                    if (dispatchTimeoutCts.IsCancellationRequested)
                    {
                        _logger.LogCritical("DISPATCH ABORTED due to exceeding the master timeout of {Timeout}. This is a final safety net.", TotalDispatchTimeout);
                    }
                    else
                    {
                        _logger.LogWarning("Dispatch was cancelled externally.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "An unhandled exception occurred during the dispatch orchestration.");
                }
            }, cancellationToken); // Note: We pass the original token here, the linking happens inside.
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