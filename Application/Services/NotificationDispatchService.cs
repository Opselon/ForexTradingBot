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
        public async Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            // Retrieve the news item. Potential database interaction.
            // Added try-catch for database call as discussed previously
            NewsItem? newsItem;
            try
            {
                newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve news item {NewsItemId}.", newsItemId);
                // Depending on error handling strategy, could re-throw a specific exception
                return; // Cannot dispatch if news item cannot be retrieved
            }


            if (newsItem == null)
            {
                _logger.LogWarning("News item with ID {NewsItemId} not found. Cannot dispatch.", newsItemId);
                return; // Exit if news item is not found
            }

            // Use a logging scope for better traceability
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["NewsItemId"] = newsItem.Id,
                ["NewsTitleScope"] = newsItem.Title?.Truncate(50) ?? "No Title" // Log truncated title
            }))
            {
                _logger.LogInformation("Initiating BATCH notification dispatch for news item.");

                // Fetch target users based on news item criteria. Potential database interaction.
                IEnumerable<User> targetUsers;
                try
                {
                    targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                        newsItem.AssociatedSignalCategoryId,
                        newsItem.IsVipOnly,
                        cancellationToken
                    );
                }
                catch (Exception ex)
                {
                    // Log the error but do not re-throw, as the goal is to dispatch if possible.
                    _logger.LogError(ex, "Failed to retrieve target users for news dispatch.");
                    return; // Cannot proceed without users
                }

                // Convert to list and check if any users were found
                var targetUserList = targetUsers?.ToList() ?? [];
                if (!targetUserList.Any())
                {
                    _logger.LogInformation("No target users found for news item matching criteria.");
                    return; // Exit if no users match
                }

                _logger.LogInformation("Fetched {UserCount} total users eligible for notification. Now chunking into batches of {BatchSize}.", targetUserList.Count, BatchSize);

                // 1. Chunk users into batches for sending
                // Filter for valid Telegram IDs (long) and then chunk. Requires .NET 6+ for Chunk.
                var userBatches = targetUserList
                    .Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null) // Safely parse TelegramId to long
                    .Where(id => id.HasValue) // Keep only successful parses
                    .Select(id => id.Value) // Get the long value
                    .Chunk(BatchSize); // Divide into batches of BatchSize

                // Extract common message parameters once
                string messageText = BuildMessageText(newsItem); // Assume this method builds the MarkdownV2 text
                string? imageUrl = newsItem.ImageUrl;
                var buttons = BuildNotificationButtons(newsItem); // Assume this method builds InlineKeyboardMarkup
                var categoryId = newsItem.AssociatedSignalCategoryId;
                var categoryName = newsItem.AssociatedSignalCategory?.Name ?? string.Empty; // Added null check

                int totalUsersEnqueued = 0;
                int batchNumber = 1;

                // Define the delay duration between enqueuing each batch job
                // ADJUST THIS VALUE based on desired overall speed and Telegram rate limits at the job scheduling level.
                // This is NOT the primary place to handle Telegram's message-per-second rate limit.
                // The optimal place is *within* SendBatchNotificationAsync (between individual messages).
                TimeSpan delayBetweenBatchEnqueues = TimeSpan.FromSeconds(5); // Example: Delay enqueuing next job by 500ms

                // 2. Enqueue a job for each batch
                foreach (var userBatch in userBatches)
                {
                    // Check for cancellation before enqueuing each batch's job
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Dispatch process was cancelled while enqueuing batches. {TotalUsersEnqueued} users were enqueued in {BatchCount} batches so far.", totalUsersEnqueued, batchNumber - 1);
                        break; // Exit the loop if cancellation is requested
                    }

                    // Ensure the batch is not empty after filtering invalid Telegram IDs
                    if (!userBatch.Any())
                    {
                        _logger.LogWarning("Batch #{BatchNumber} is empty after filtering. Skipping.", batchNumber);
                        batchNumber++;
                        continue; // Skip empty batches
                    }


                    try
                    {

                        // Enqueue the job to send notifications for the current batch of users.
                        // The actual delay between sending messages to *individual users* within the batch
                        // should ideally be handled *inside* SendBatchNotificationAsync or by JobScheduler configuration.
                        string jobId = _jobScheduler.Enqueue<INotificationSendingService>(
                            service => service.SendBatchNotificationAsync(
                                userBatch.ToList(), // Pass the list of user Telegram IDs for this batch
                                messageText,
                                imageUrl,
                                buttons,
                                newsItem.Id,
                                categoryId,
                                categoryName,
                                CancellationToken.None // Pass CancellationToken.None if job cancellation is independent
                                                       // Or pass 'cancellationToken' if job should respect overall dispatch cancellation
                            )
                        );
                        _logger.LogInformation("Enqueued batch job {JobId} for {UserCount} users (Batch #{BatchNumber}).",
                                               jobId, userBatch.Length, batchNumber);
                        totalUsersEnqueued += userBatch.Length;
                    }
                    catch (Exception ex)
                    {
                        // Log the error if enqueuing a job fails, but continue with other batches.
                        _logger.LogError(ex, "Failed to enqueue batch job #{BatchNumber} for {UserCount} users. This batch will be skipped.", batchNumber, userBatch.Length);
                    }

                    // --- Apply Delay Between Enqueuing Batches ---
                    // This delays the *creation/scheduling* of the next job.
                    // It helps space out the jobs if your scheduler runs them immediately.
                    // Use Task.Delay and pass the cancellation token so the delay can be interrupted.
                    // We delay before the *next* batch, so we check if there *is* a next batch (current batch number is less than total batches).
                    if (batchNumber < userBatches.Count())
                    {
                        try
                        {
                            // Wait for the specified delay duration.
                            await Task.Delay(delayBetweenBatchEnqueues, cancellationToken);
                            _logger.LogTrace("Delayed {DelayTotalSeconds} seconds before enqueuing batch #{NextBatchNumber}.", delayBetweenBatchEnqueues.TotalSeconds, batchNumber + 1);
                        }
                        catch (TaskCanceledException)
                        {
                            // Catch cancellation specifically for the delay task.
                            _logger.LogWarning("Delay between batch enqueues was cancelled.");
                            // The main cancellation check at the start of the loop will handle exiting.
                        }
                    }
                    // --------------------------------------------

                    batchNumber++;
                }

                _logger.LogInformation("Completed enqueuing all batches. Total jobs created: {BatchCount}. Total users enqueued: {TotalUsersEnqueued}.", batchNumber - 1, totalUsersEnqueued);
            }
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