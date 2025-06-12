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

        /// <summary>
        /// Asynchronously dispatches notifications for a specified news item to eligible users.
        /// This involves fetching users, chunking them into batches, and enqueuing jobs
        /// to send notifications for each batch, with an optional delay between enqueuing batches
        /// to manage load or adhere to potential rate limits at the job scheduling level.
        /// </summary>
        /// <param name="newsItemId">The unique identifier of the news item to dispatch notifications for.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            // Offload the entire dispatch orchestration to a ThreadPool thread.
            // This ensures the calling thread (e.g., an ASP.NET request thread) is not blocked
            // by the potentially long-running process of enqueuing all batches,
            // especially due to the Task.Delay between enqueuing batches.
            return Task.Run(async () =>
            {
                NewsItem? newsItem;
                try
                {
                    newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("News item retrieval for {NewsItemId} was cancelled.", newsItemId);
                    throw; // Propagate to ensure the Task returned by Task.Run is Canceled.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to retrieve news item {NewsItemId}.", newsItemId);
                    // Exit the lambda; the Task from Task.Run will complete as RanToCompletion.
                    // If this background task's failure should be more visible, consider re-throwing.
                    return;
                }

                if (newsItem == null)
                {
                    _logger.LogWarning("News item with ID {NewsItemId} not found. Cannot dispatch.", newsItemId);
                    return;
                }

                // Ensure cancellation is checked after operations if they don't internally throw OperationCanceledException.
                cancellationToken.ThrowIfCancellationRequested();

                // Use a logging scope for better traceability
                using (_logger.BeginScope(new Dictionary<string, object?>
                {
                    ["NewsItemId"] = newsItem.Id,
                    // Assuming a Truncate extension method exists for strings
                    ["NewsTitleScope"] = newsItem.Title?.Length > 50 ? newsItem.Title.Substring(0, 50) : newsItem.Title ?? "No Title"
                }))
                {
                    _logger.LogInformation("Initiating BATCH notification dispatch for news item.");

                    IEnumerable<User> targetUsers;
                    try
                    {
                        targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                            newsItem.AssociatedSignalCategoryId,
                            newsItem.IsVipOnly,
                            cancellationToken
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Target user retrieval for news item {NewsItemId} was cancelled.", newsItemId);
                        throw; // Propagate to ensure the Task returned by Task.Run is Canceled.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to retrieve target users for news dispatch.");
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var targetUserList = targetUsers?.ToList() ?? [];
                    if (!targetUserList.Any())
                    {
                        _logger.LogInformation("No target users found for news item matching criteria.");
                        return;
                    }

                    _logger.LogInformation("Fetched {UserCount} total users eligible for notification. Now chunking into batches of {BatchSize}.", targetUserList.Count, BatchSize);

                    var userBatches = targetUserList
                        .Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null)
                        .Where(id => id.HasValue)
                        .Select(id => id.Value)
                        .Chunk(BatchSize); // .Chunk() is available in .NET 6+

                    string messageText = BuildMessageText(newsItem);
                    string? imageUrl = newsItem.ImageUrl;
                    var buttons = BuildNotificationButtons(newsItem); // Ensure this returns the correct type
                    var categoryId = newsItem.AssociatedSignalCategoryId;
                    // Assuming SignalCategory is a related entity that might be loaded with NewsItem
                    var categoryName = newsItem.AssociatedSignalCategory?.Name ?? string.Empty;

                    int totalUsersEnqueued = 0;
                    int batchNumber = 1;
                    // Calculate total batches once if userBatches is a deferred execution LINQ query
                    // and .Count() would re-iterate. .Chunk() typically returns an IEnumerable<T[]>,
                    // where .Count() would iterate.
                    var userBatchesList = userBatches.ToList();
                    int totalBatches = userBatchesList.Count;

                    TimeSpan delayBetweenBatchEnqueues = TimeSpan.FromSeconds(15);

                    foreach (var userBatch in userBatchesList)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); // Check at the start of each iteration.

                        if (!userBatch.Any())
                        {
                            _logger.LogWarning("Batch #{BatchNumber} is empty after filtering. Skipping.", batchNumber);
                            batchNumber++;
                            continue;
                        }

                        try
                        {
                            string jobId = _jobScheduler.Enqueue<INotificationSendingService>(
                                service => service.SendBatchNotificationAsync(
                                    userBatch.ToList(), // Pass the list of user Telegram IDs for this batch
                                    messageText,
                                    imageUrl,
                                    buttons,
                                    newsItem.Id,
                                    categoryId,
                                    categoryName,
                                    CancellationToken.None // As per original: job cancellation is independent
                                )
                            );
                            _logger.LogInformation("Enqueued batch job {JobId} for {UserCount} users (Batch #{BatchNumber}).",
                                                   jobId, userBatch.Length, batchNumber);
                            totalUsersEnqueued += userBatch.Length;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to enqueue batch job #{BatchNumber} for {UserCount} users. This batch will be skipped.", batchNumber, userBatch.Length);
                        }

                        if (batchNumber < totalBatches) // Only delay if there are more batches to enqueue
                        {
                            // Task.Delay will throw OperationCanceledException if 'cancellationToken' is signaled.
                            // This exception will propagate out, causing the Task from Task.Run to be Canceled.
                            await Task.Delay(delayBetweenBatchEnqueues, cancellationToken);
                            _logger.LogTrace("Delayed {DelayTotalSeconds} seconds before enqueuing batch #{NextBatchNumber}.", delayBetweenBatchEnqueues.TotalSeconds, batchNumber + 1);
                        }
                        batchNumber++;
                    }
                    _logger.LogInformation("Completed enqueuing all batches. Total jobs created: {BatchCount}. Total users enqueued: {TotalUsersEnqueued}.", batchNumber - 1, totalUsersEnqueued);
                }
            }, cancellationToken); // Pass CancellationToken to Task.Run for cooperative cancellation.
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

            // Using the updated escaping method to handle Markdown for Telegram.
            // Truncation limit for summary is kept at 250 as in original.
            string title = EscapeTextForTelegramMarkup(newsItem.Title?.Trim() ?? "Untitled News");
            string sourceName = EscapeTextForTelegramMarkup(newsItem.SourceName?.Trim() ?? "Unknown Source");
            string summary = EscapeTextForTelegramMarkup(TruncateWithEllipsis(newsItem.Summary, 250)?.Trim() ?? string.Empty);
            string? link = newsItem.Link?.Trim();

            _ = messageTextBuilder.AppendLine($"*{title}*"); // Bold for Telegram Markdown (V1/relaxed V2)
            _ = messageTextBuilder.AppendLine($"_📰 Source: {sourceName}_"); // Italic for Telegram Markdown

            if (!string.IsNullOrWhiteSpace(summary))
            {
                _ = messageTextBuilder.Append($"\n{summary}");
            }

            if (!string.IsNullOrWhiteSpace(link))
            {
                if (Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    // For links in Telegram Markdown, parentheses within the URL must be escaped.
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
        private List<NotificationButton> BuildNotificationButtons(NewsItem newsItem)
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