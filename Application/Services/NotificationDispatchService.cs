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
        /// </summary>
        /// <param name="newsItemId">The unique identifier of the news item to dispatch notifications for.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
            if (newsItem == null)
            {
                _logger.LogWarning("News item with ID {NewsItemId} not found. Cannot dispatch.", newsItemId);
                return;
            }

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["NewsItemId"] = newsItem.Id,
                ["NewsTitleScope"] = newsItem.Title?.Truncate(50) ?? "No Title"
            }))
            {
                _logger.LogInformation("Initiating BATCH notification dispatch for news item.");

                // دریافت کل کاربران (بدون تغییر در Repository)
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

                var targetUserList = targetUsers?.ToList() ?? new List<User>();
                if (!targetUserList.Any())
                {
                    _logger.LogInformation("No target users found for news item.");
                    return;
                }

                _logger.LogInformation("Fetched {UserCount} total users. Now chunking into batches of {BatchSize}.", targetUserList.Count, BatchSize);

                // 1. دسته‌بندی کاربران (Chunking)
                // ابتدا شناسه‌های تلگرام معتبر را استخراج کرده و سپس آن‌ها را دسته‌بندی می‌کنیم.
                // متد Chunk نیازمند .NET 6 یا بالاتر است.
                var userBatches = targetUserList
                    .Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .Chunk(BatchSize);

                // استخراج پارامترهای مشترک پیام (یک بار)
                string messageText = BuildMessageText(newsItem);
                string? imageUrl = newsItem.ImageUrl;
                var buttons = BuildNotificationButtons(newsItem);
                var categoryId = newsItem.AssociatedSignalCategoryId;
                var categoryName = newsItem.AssociatedSignalCategory?.Name ?? string.Empty;

                int totalUsersEnqueued = 0;
                int batchNumber = 1;

                // 2. ایجاد یک جاب برای هر دسته
                foreach (var userBatch in userBatches)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Dispatch process was cancelled. {TotalUsersEnqueued} users were enqueued in batches.", totalUsersEnqueued);
                        break;
                    }

                    try
                    {
                        // فراخوانی متد جدید برای ارسال دسته‌ای
                        string jobId = _jobScheduler.Enqueue<INotificationSendingService>(
                            service => service.SendBatchNotificationAsync(
                                userBatch.ToList(),
                                messageText,
                                imageUrl,
                                buttons,
                                newsItem.Id,
                                categoryId,
                                categoryName,
                                CancellationToken.None
                            )
                        );
                        _logger.LogInformation("Enqueued batch job {JobId} for {UserCount} users (Batch #{BatchNumber}).",
                                               jobId, userBatch.Length, batchNumber);
                        totalUsersEnqueued += userBatch.Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to enqueue batch job #{BatchNumber}. This batch of {UserCount} users will be skipped.", batchNumber, userBatch.Length);
                    }
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