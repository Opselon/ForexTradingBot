// File: Application/Services/NotificationDispatchService.cs
// File: Application/Services/NotificationDispatchService.cs

#region Usings
// Standard .NET & NuGet
// Project specific: Application Layer
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces; // For INotificationDispatchService and INotificationSendingService
// Project specific: Domain Layer
using Domain.Entities;      // For NewsItem, User
using Microsoft.Extensions.Logging; // For ILogger and BeginScope
using Shared.Extensions;    // For .Truncate()
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text; // Required for StringBuilder
using System.Threading;
using System.Threading.Tasks;
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
        // private readonly IUserSignalPreferenceRepository _userPrefsRepository; // Potentially used for deeper category filtering if GetUsersForNewsNotificationAsync isn't exhaustive
        #endregion

        #region Constructor
        public NotificationDispatchService(
            INewsItemRepository newsItemRepository,
            IUserRepository userRepository,
            INotificationJobScheduler jobScheduler,
            ILogger<NotificationDispatchService> logger)
        // IUserSignalPreferenceRepository userPrefsRepository)
        {
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // _userPrefsRepository = userPrefsRepository ?? throw new ArgumentNullException(nameof(userPrefsRepository));
        }
        #endregion

        #region INotificationDispatchService Implementation

        /// <summary>
        /// Asynchronously dispatches notifications for a specified news item to eligible users.
        /// Retrieves the news item, identifies target users based on their notification preferences and VIP status,
        /// constructs a notification payload, and enqueues it for background processing via a job scheduler.
        /// Includes comprehensive logging, error handling, and cancellation support.
        /// </summary>
        /// <param name="newsItemId">The unique identifier of the news item to dispatch notifications for.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <remarks>
        /// Performance Considerations:
        /// - Relies on an optimized `_userRepository.GetUsersForNewsNotificationAsync` to fetch users efficiently.
        /// - Iterates users serially; for extremely large user bases (>10k-100k simultaneously),
        ///   consider parallelizing the payload creation and enqueuing, balancing throughput with complexity and resource use.
        /// - `_jobScheduler.Enqueue` is assumed to be a non-blocking or fast operation (e.g., writing to a message queue).
        ///
        /// Security Considerations:
        /// - Message text is constructed here. If dynamic content from `NewsItem` might contain Markdown special characters,
        ///   it should be properly escaped using a robust `EscapeMarkdownV2` utility to prevent formatting issues or injection if `UseMarkdown` is true.
        /// - `CallbackDataOrUrl` for buttons should be validated or generated securely if it contains user-specific or sensitive parts,
        ///   though in this case it's a direct link from NewsItem.
        ///
        /// Robustness:
        /// - Handles null `NewsItem` and invalid `User.TelegramId`.
        /// - Uses `CancellationToken` to gracefully stop dispatch if requested.
        /// - Individual job enqueue failures are logged but do not stop the dispatch for other users.
        /// </remarks>
        public async Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            #region Retrieve and Validate NewsItem
            // Performance: Single async call to fetch news item.
            NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
            if (newsItem == null)
            {
                // Robustness: Handles case where news item is not found or was deleted.
                _logger.LogWarning("News item with ID {NewsItemId} not found. Cannot dispatch notifications.", newsItemId);
                return;
            }
            #endregion

            // Readability: Using structured logging scope for all logs related to this news item dispatch.
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["NewsItemId"] = newsItem.Id,
                ["NewsTitleScope"] = newsItem.Title?.Truncate(50) ?? "No Title", // Handle null title
                ["NewsItemIsVip"] = newsItem.IsVipOnly,
                ["NewsItemCategoryId"] = newsItem.AssociatedSignalCategoryId
            }))
            {
                _logger.LogInformation("Initiating notification dispatch for news item.");

                #region Fetch Target Users
                // Performance: This is a critical data retrieval step.
                // Assumes `GetUsersForNewsNotificationAsync` is optimized to perform efficient filtering
                // at the database level based on `newsItem.AssociatedSignalCategoryId` and `newsItem.IsVipOnly`.
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
                    // Robustness: Catching potential errors during user retrieval.
                    _logger.LogError(ex, "Failed to retrieve target users for news dispatch.");
                    return; // Exit if users cannot be reliably fetched.
                }

                // Ensure targetUsers is not null to prevent NullReferenceException in .Any() or .Count().
                // `GetUsersForNewsNotificationAsync` should ideally return an empty collection, not null.
                if (targetUsers == null)
                {
                    _logger.LogError("User repository returned null for targetUsers, which is unexpected. Assuming no users.");
                    targetUsers = Enumerable.Empty<User>();
                }

                // ToList() to avoid multiple enumerations if Count() and iteration are both needed.
                // And to get a concrete count for logging.
                // Performance: If targetUsers can be extremely large and memory is a concern,
                // avoid ToList() and use other means or accept potential multiple enumerations
                // if the source is an IQueryable that re-queries.
                // For most cases with IEnumerable from a repository (already in memory or efficiently streamed), this is fine.
                var targetUserList = targetUsers.ToList();

                if (!targetUserList.Any())
                {
                    _logger.LogInformation("No target users found for news item based on preferences/subscriptions.");
                    return;
                }
                _logger.LogInformation("Identified {UserCount} target users for news item.", targetUserList.Count);
                #endregion

                #region Prepare and Enqueue Notification Jobs
                int dispatchedCount = 0;
                int skippedInvalidTelegramIdCount = 0;

                // Design Choice: Message construction is done once per news item, not per user,
                // if the core message is the same. User-specific parts would be added later or by NotificationSendingService.
                // Here, the message is constructed within the loop if user-specific content (e.g. name) was needed,
                // or just once if generic. The current structure implies a generic message per news item.
                // Let's assume we want to build the core message once for efficiency if no user-specific parts from 'user' object are in the main text.

                string messageText = BuildMessageText(newsItem); // Helper method to construct the message
                string? imageUrl = newsItem.ImageUrl;            // Optional image URL
                var buttons = BuildNotificationButtons(newsItem); // Helper method for buttons

                // Iterating through the identified target users.
                foreach (var user in targetUserList)
                {
                    // Robustness: Check for cancellation at each iteration.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Notification dispatch process cancelled by request. {DispatchedCount} jobs enqueued.", dispatchedCount);
                        break; // Exit the loop.
                    }

                    // Robustness: Validate Telegram ID format.
                    if (string.IsNullOrWhiteSpace(user.TelegramId) || !long.TryParse(user.TelegramId, out long telegramUserId))
                    {
                        _logger.LogWarning("User {UserId} (System Username: {SystemUsername}) has an invalid or missing TelegramId ('{UserTelegramId}'). Skipping notification.",
                                           user.Id, user.Username, user.TelegramId);
                        skippedInvalidTelegramIdCount++;
                        continue;
                    }

                    var payload = new NotificationJobPayload
                    {
                        TargetTelegramUserId = telegramUserId,
                        MessageText = messageText, // Using pre-built message
                        UseMarkdown = true,        // Assuming Markdown. Escape source text appropriately.
                        ImageUrl = imageUrl,
                        NewsItemId = newsItem.Id,
                        NewsItemSignalCategoryId = newsItem.AssociatedSignalCategoryId,
                        NewsItemSignalCategoryName = newsItem.AssociatedSignalCategory?.Name ?? string.Empty, // Handle null category name
                        Buttons = buttons, // Using pre-built buttons
                        CustomData = new Dictionary<string, string> { { "NewsItemId", newsItem.Id.ToString() } }
                        // Consider adding UserId, Username to CustomData if NotificationSendingService needs them for advanced logic.
                    };

                    try
                    {
                        // Performance: `Enqueue` should ideally be a fast, non-blocking operation.
                        // For Hangfire, this usually involves serializing the payload and writing to storage.
                        // CancellationToken.None is appropriate as the job's lifecycle is independent of this dispatch.
                        string jobId = _jobScheduler.Enqueue<INotificationSendingService>(
                            sendingService => sendingService.SendNotificationAsync(payload, CancellationToken.None)
                        );

                        _logger.LogInformation("Enqueued notification job {JobId} for User (SystemID: {SystemUserId}, TG_ID: {TelegramUserId}).",
                                               jobId, user.Id, telegramUserId);
                        dispatchedCount++;
                    }
                    catch (Exception ex)
                    {
                        // Robustness: Log failure to enqueue for a specific user but continue with others.
                        _logger.LogError(ex, "Failed to enqueue notification job for User (SystemID: {SystemUserId}, TG_ID: {TelegramUserId}). Payload: {@NotificationPayload}",
                                         user.Id, telegramUserId, payload); // Log payload on error for diagnosis. Be cautious of sensitive data in payload.
                    }
                } // End foreach user

                _logger.LogInformation("Notification dispatch completed. Total jobs enqueued: {DispatchedCount}. Users skipped due to invalid TelegramId: {SkippedCount}.",
                                       dispatchedCount, skippedInvalidTelegramIdCount);
                #endregion
            } // End using _logger.BeginScope
        }

        /// <summary>
        /// Builds the main text content for a news notification.
        /// </summary>
        /// <param name="newsItem">The news item to generate text for.</param>
        /// <returns>Formatted string for the notification message.</returns>
        /// <remarks>
        /// Security: Ensure all interpolated strings from `newsItem` (Title, SourceName, Summary, Link)
        /// are appropriately escaped for MarkdownV2 if `UseMarkdown` is true on the payload.
        /// The current `EscapeMarkdownV2Lenient` method provides basic escaping. For production, a more robust solution is recommended.
        /// </remarks>
        private string BuildMessageText(NewsItem newsItem)
        {
            if (newsItem == null)
            {
                throw new ArgumentNullException(nameof(newsItem));
            }

            var messageTextBuilder = new StringBuilder();

            // Security & Robustness: Null checks and trimming for all text parts.
            // Using a more lenient V2 escaper to avoid breaking valid links or overly aggressive escaping.
            string title = EscapeMarkdownV2Lenient(newsItem.Title?.Trim() ?? "Untitled News");
            string sourceName = EscapeMarkdownV2Lenient(newsItem.SourceName?.Trim() ?? "Unknown Source");
            string summary = EscapeMarkdownV2Lenient(TruncateWithEllipsis(newsItem.Summary, 250)?.Trim() ?? string.Empty); // Max length 250
            string? link = newsItem.Link?.Trim(); // URLs in Markdown links generally don't need escaping for V2 unless they contain ')' or '\'.

            messageTextBuilder.AppendLine($"*{title}*"); // Title bold
            messageTextBuilder.AppendLine($"_📰 Source: {sourceName}_"); // Source italic

            if (!string.IsNullOrWhiteSpace(summary))
            {
                messageTextBuilder.Append($"\n{summary}"); // Summary directly, new line before.
            }

            if (!string.IsNullOrWhiteSpace(link))
            {
                // Ensure link is a valid URL before attempting to create a Markdown link
                if (Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    // For links in MarkdownV2, parentheses within the URL part must be escaped: '(' becomes '\(', ')' becomes '\)'
                    string escapedLink = link.Replace("(", "\\(").Replace(")", "\\)");
                    messageTextBuilder.Append($"\n\n[🔗 Read Full Article]({escapedLink})");
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
            // Potentially add other common buttons, e.g., "Share", or category-specific actions.
            // User-specific buttons like "Subscribe/Unsubscribe to this category" should be handled
            // in INotificationSendingService as it has user context and can check preferences.
            return buttons;
        }

        /// <summary>
        /// Truncates text to a maximum length, appending ellipsis if truncated.
        /// Handles null or whitespace input.
        /// </summary>
        private string? TruncateWithEllipsis(string? text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength - 3) + "...";
        }


        /// <summary>
        /// Escapes characters that have special meaning in Telegram MarkdownV2.
        /// This is a simplified/lenient version. For robust escaping, consider a library or official regex.
        /// See: https://core.telegram.org/bots/api#markdownv2-style
        /// Characters: '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!'
        /// </summary>
        private string EscapeMarkdownV2Lenient(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // More targeted replacement for V2 common pitfalls.
            // Not exhaustive but covers many cases.
            var sb = new StringBuilder(text.Length + 10); // Initial capacity
            foreach (char c in text)
            {
                switch (c)
                {
                    case '_':
                    case '*':
                    case '[':
                    case ']':
                    case '(':
                    case ')':
                    case '~':
                    case '`':
                    case '>':
                    case '#':
                    case '+':
                    case '-':
                    case '=':
                    case '|':
                    case '{':
                    case '}':
                    case '.':
                    case '!':
                        sb.Append('\\');
                        break;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        #endregion
    }
}

