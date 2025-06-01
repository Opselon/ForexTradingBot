// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Shared.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        /// </summary>
        /// <param name="newsItemId">The unique identifier of the news item to dispatch notifications for.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
            if (newsItem == null)
            {
                _logger.LogWarning("News item with ID {NewsItemId} not found. Cannot dispatch notifications.", newsItemId);
                return;
            }

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["NewsItemId"] = newsItem.Id,
                ["NewsTitleScope"] = newsItem.Title?.Truncate(50) ?? "No Title",
                ["NewsItemIsVip"] = newsItem.IsVipOnly,
                ["NewsItemCategoryId"] = newsItem.AssociatedSignalCategoryId
            }))
            {
                _logger.LogInformation("Initiating notification dispatch for news item.");

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
                    _logger.LogError(ex, "Failed to retrieve target users for news dispatch.");
                    return;
                }

                if (targetUsers == null)
                {
                    _logger.LogError("User repository returned null for targetUsers, which is unexpected. Assuming no users.");
                    targetUsers = Enumerable.Empty<User>();
                }

                var targetUserList = targetUsers.ToList();

                if (!targetUserList.Any())
                {
                    _logger.LogInformation("No target users found for news item based on preferences/subscriptions.");
                    return;
                }
                _logger.LogInformation("Identified {UserCount} target users for news item.", targetUserList.Count);

                int dispatchedCount = 0;
                int skippedInvalidTelegramIdCount = 0;

                string messageText = BuildMessageText(newsItem);
                string? imageUrl = newsItem.ImageUrl;
                var buttons = BuildNotificationButtons(newsItem);

                foreach (var user in targetUserList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Notification dispatch process cancelled by request. {DispatchedCount} jobs enqueued.", dispatchedCount);
                        break;
                    }

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
                        MessageText = messageText,
                        UseMarkdown = true, // Keep true, tells Telegram API to parse Markdown.
                        ImageUrl = imageUrl,
                        NewsItemId = newsItem.Id,
                        NewsItemSignalCategoryId = newsItem.AssociatedSignalCategoryId,
                        NewsItemSignalCategoryName = newsItem.AssociatedSignalCategory?.Name ?? string.Empty,
                        Buttons = buttons,
                        CustomData = new Dictionary<string, string> { { "NewsItemId", newsItem.Id.ToString() } }
                    };

                    try
                    {
                        string jobId = _jobScheduler.Enqueue<INotificationSendingService>(
                            sendingService => sendingService.SendNotificationAsync(payload, CancellationToken.None)
                        );

                        _logger.LogInformation("Enqueued notification job {JobId} for User (SystemID: {SystemUserId}, TG_ID: {TelegramUserId}).",
                                               jobId, user.Id, telegramUserId);
                        dispatchedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to enqueue notification job for User (SystemID: {SystemUserId}, TG_ID: {TelegramUserId}). Payload: {@NotificationPayload}",
                                         user.Id, telegramUserId, payload);
                    }
                }

                _logger.LogInformation("Notification dispatch completed. Total jobs enqueued: {DispatchedCount}. Users skipped due to invalid TelegramId: {SkippedCount}.",
                                       dispatchedCount, skippedInvalidTelegramIdCount);
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

            messageTextBuilder.AppendLine($"*{title}*"); // Bold for Telegram Markdown (V1/relaxed V2)
            messageTextBuilder.AppendLine($"_📰 Source: {sourceName}_"); // Italic for Telegram Markdown

            if (!string.IsNullOrWhiteSpace(summary))
            {
                messageTextBuilder.Append($"\n{summary}");
            }

            if (!string.IsNullOrWhiteSpace(link))
            {
                if (Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    // For links in Telegram Markdown, parentheses within the URL must be escaped.
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
            return buttons;
        }

        /// <summary>
        /// Truncates text to a maximum length, appending ellipsis if truncated.
        /// </summary>
        private string? TruncateWithEllipsis(string? text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Escapes characters in plain text that have special meaning in Telegram Markdown (V1/relaxed V2 compatible).
        /// This method targets minimal necessary escaping to produce clean output without extraneous backslashes
        /// on common punctuation like periods, hyphens, or slashes, as seen in the user's desired output format.
        /// </summary>
        private string EscapeTextForTelegramMarkup(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

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