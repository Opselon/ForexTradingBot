// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums; // For UserLevel enum
using Hangfire;
using Polly;
using Polly.Retry;
using Shared.Extensions;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;
using Telegram.Bot;

// Telegram.Bot
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Formatters; // For TelegramMessageFormatter
using TelegramPanel.Infrastructure;
#endregion

namespace BackgroundTasks.Services
{
    /// <summary>
    /// Implements the Hangfire job for sending notifications using a cache-first strategy
    /// with a focus on responsible, non-spam communication.
    /// </summary>
    public class NotificationSendingService : INotificationSendingService
    {
        #region Dependencies & Configuration
        private readonly ILogger<NotificationSendingService> _logger;
        private readonly ITelegramMessageSender _telegramMessageSender;
        private readonly IUserService _userService;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly IDatabase _redisDb;
        private readonly INotificationRateLimiter _rateLimiter;
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy;
        private readonly ITelegramBotClient _botClient;
        private const int FreeUserRssHourlyLimit = 20; // As per your new requirement
        private const int VipUserRssHourlyLimit = 100;  // Example: VIP users get a higher limi
        private const int RegularUserHourlyLimit = 5;
        private const int VipUserHourlyLimit = 20;
        private static readonly TimeSpan LimitPeriod = TimeSpan.FromHours(1);
        private static readonly TimeSpan RssLimitPeriod = TimeSpan.FromHours(1);
        #endregion

        #region Constructor
        public NotificationSendingService(
            ILogger<NotificationSendingService> logger,
             ITelegramBotClient botClient, 
            ITelegramMessageSender telegramMessageSender,
            IUserService userService,
            INewsItemRepository newsItemRepository,
            IConnectionMultiplexer redisConnection,
            INotificationRateLimiter rateLimiter)
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

            if (redisConnection == null) throw new ArgumentNullException(nameof(redisConnection));
            _redisDb = redisConnection.GetDatabase();

            _telegramApiRetryPolicy = Policy
                .Handle<ApiRequestException>(ex => ex.ErrorCode == 429) // Rate limit
                .Or<HttpRequestException>() // Network error
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
        }
        #endregion


        [Obsolete("This method is deprecated. Use the cache-first dispatch pattern instead.", true)]
        public Task SendBatchNotificationAsync(List<long> targetTelegramUserIdList, string messageText, string? imageUrl, List<NotificationButton> buttons, Guid newsItemId, Guid? newsItemSignalCategoryId, string? newsItemSignalCategoryName, CancellationToken jobCancellationToken)
        {
            _logger.LogWarning("Obsolete method SendBatchNotificationAsync was called and will do nothing.");
            // This method now does nothing and completes immediately.
            return Task.CompletedTask;
        }


        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("Send Batch of {1.Count} News Items to User: {0}")]
        public async Task ProcessBatchNotificationForUserAsync(long targetUserId, List<Guid> newsItemIds)
        {
            try
            {
                if (newsItemIds == null || !newsItemIds.Any())
                {
                    _logger.LogWarning("Job for user {UserId} received an empty list of news IDs.", targetUserId);
                    return;
                }

                // 1. Fetch all news item entities from the database.
                var newsItems = new List<NewsItem>();
                foreach (var id in newsItemIds)
                {
                    var item = await _newsItemRepository.GetByIdAsync(id, CancellationToken.None);
                    if (item != null)
                    {
                        newsItems.Add(item);
                    }
                }

                if (!newsItems.Any())
                {
                    _logger.LogWarning("No valid news items found for batch send to user {UserId}. Aborting job.", targetUserId);
                    return;
                }

                // 2. Build ONE consolidated message from all the news items.
                var messageBuilder = new StringBuilder();
                messageBuilder.AppendLine($"*{newsItems.Count} new updates for you:*");
                messageBuilder.AppendLine();

                // To keep the message from getting too long, we'll only show details for the first few.
                foreach (var item in newsItems.Take(5))
                {
                    messageBuilder.AppendLine($"▫️ *{TelegramMessageFormatter.EscapeMarkdownV2(item.Title)}*");
                    if (!string.IsNullOrWhiteSpace(item.Summary))
                    {
                        var summary = TelegramMessageFormatter.EscapeMarkdownV2(item.Summary.Truncate(120));
                        messageBuilder.AppendLine($"  _{summary}_");
                    }
                    messageBuilder.AppendLine();
                }

                if (newsItems.Count > 5)
                {
                    messageBuilder.AppendLine($"...and {newsItems.Count - 5} more articles.");
                }

                // 3. Prepare the final payload for the sender method.
                var payload = new NotificationJobPayload
                {
                    TargetTelegramUserId = targetUserId,
                    MessageText = messageBuilder.ToString(),
                    Buttons = new List<NotificationButton>() // Batch notifications usually don't have specific buttons.
                };

                // 4. Call the final "sender" helper to send the message to the Telegram API.
                await ProcessSingleNotification(payload, CancellationToken.None);

                _logger.LogInformation("Successfully sent a batch of {Count} news items to user {UserId}", newsItems.Count, targetUserId);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to process batch notification for user {UserId}", targetUserId);
                throw; // Re-throw so Hangfire marks the job as Failed.
            }
        }

        /// <summary>
        /// The primary Hangfire job method. Implements safeguards before sending a notification.
        /// </summary>
        // ✅✅ THIS IS THE METHOD TO FIX ✅✅
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        [JobDisplayName("Process RSS Notification: News {0}, UserIndex {2}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task ProcessNotificationFromCacheAsync(Guid newsItemId, string userListCacheKey, int userIndex)
        {
            // --- Configuration: Define business rules for this job ---
            const int freeUserRssHourlyLimit = 20;
            const int vipUserRssHourlyLimit = 100;
            var rssLimitPeriod = TimeSpan.FromHours(1);

            // Initialize targetUserId to a known invalid state for better error logging in the final catch block.
            long targetUserId = -1;

            // Use a logging scope to automatically add these details to all logs within this method.
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["NewsItemId"] = newsItemId,
                ["UserIndex"] = userIndex,
                ["CacheKey"] = userListCacheKey
            });

            try
            {
                // STEP 1: Retrieve this job's assigned User ID from the Redis cache.
                // This logic is now self-contained and clear.
                var serializedUserIds = await _redisDb.StringGetAsync(userListCacheKey);
                if (!serializedUserIds.HasValue)
                {
                    _logger.LogError("Job aborted: Cache key not found. It may have expired or was never set.");
                    return; // Gracefully stop the job.
                }

                var allUserIds = JsonSerializer.Deserialize<List<long>>(serializedUserIds);
                if (allUserIds == null || userIndex >= allUserIds.Count)
                {
                    _logger.LogError("Job aborted: User index is out of bounds for the cached list (Size: {ListSize}).", allUserIds?.Count ?? 0);
                    return; // Gracefully stop the job.
                }

                targetUserId = allUserIds[userIndex];
                _logger.LogInformation("Processing job for UserID: {TargetUserId}", targetUserId);

                // STEP 2: Fetch the user's full profile to determine their level for rate limiting.
                // This call is fast because UserService uses its own Redis cache (Cache-Aside pattern).
                var userDto = await _userService.GetUserByTelegramIdAsync(targetUserId.ToString(), CancellationToken.None);
                if (userDto == null)
                {
                    _logger.LogWarning("Job skipped: User {TargetUserId} was in the dispatch cache but no longer exists in the database.", targetUserId);
                    return;
                }

                // STEP 3: Apply the correct rate limit based on the user's level.
                int applicableLimit = (userDto.Level == UserLevel.Platinum || userDto.Level == UserLevel.Bronze)
                    ? vipUserRssHourlyLimit
                    : freeUserRssHourlyLimit;

                if (await _rateLimiter.IsUserOverLimitAsync(targetUserId, applicableLimit, rssLimitPeriod))
                {
                    _logger.LogInformation("FAST SKIP: User {TargetUserId} (Level: {UserLevel}) has exceeded the hourly RSS limit of {Limit}.",
                        targetUserId, userDto.Level, applicableLimit);
                    return; // This is a successful, intentional stop.
                }

                // STEP 4: Fetch the news article data. This is the only required database hit for content.
                var newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, CancellationToken.None);
                if (newsItem == null)
                {
                    // If the news item was deleted between dispatch and execution, we can't proceed.
                    _logger.LogError("Job failed: NewsItem with ID {NewsItemId} could not be found.", newsItemId);
                    throw new InvalidOperationException($"NewsItem {newsItemId} not found.");
                }

                // STEP 5: Build the final, fully-formed payload.
                var payload = new NotificationJobPayload
                {
                    TargetTelegramUserId = targetUserId,
                    MessageText = BuildMessageText(newsItem),
                    UseMarkdown = true,
                    ImageUrl = newsItem.ImageUrl,
                    Buttons = BuildSimpleNotificationButtons(newsItem),
                    NewsItemId = newsItemId
                };

                // STEP 6: Hand off to the final sender method.
                await SendToTelegramAsync(payload, CancellationToken.None);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || apiEx.Message.Contains("chat not found"))
            {
                // This catch block specifically handles permanent "user blocked" errors from the Telegram API.
                _logger.LogWarning(apiEx, "Permanent Send Failure for User {TargetUserId}: Bot was blocked or chat deleted. Initiating self-healing.", targetUserId);
                try
                {
                    await _userService.MarkUserAsUnreachableAsync(targetUserId.ToString(), "BotBlockedOrChatNotFound", CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx, "Failed to mark user {TargetUserId} as unreachable after send failure.", targetUserId);
                }
                // We DO NOT re-throw here. The job has successfully handled the error condition.
            }
            catch (Exception ex)
            {
                // This is a catch-all for any other unexpected error (e.g., Redis timeout, DB error, serialization issue).
                _logger.LogCritical(ex, "A critical failure occurred during the cached notification job for User {TargetUserId}.", targetUserId);
                // We re-throw here to ensure Hangfire marks the job as "Failed" for manual inspection.
                throw;
            }
        }

        #region Private Helpers

        private async Task<long> GetUserIdFromCacheAsync(string cacheKey, int index)
        {
            var serializedUserIds = await _redisDb.StringGetAsync(cacheKey);
            if (!serializedUserIds.HasValue)
            {
                _logger.LogError("Cache key {CacheKey} not found.", cacheKey);
                return -1;
            }
            var allUserIds = JsonSerializer.Deserialize<List<long>>(serializedUserIds);
            if (allUserIds == null || index >= allUserIds.Count)
            {
                _logger.LogError("User index {Index} is out of bounds for cache key {CacheKey}.", index, cacheKey);
                return -1;
            }
            return allUserIds[index];
        }

        private InlineKeyboardMarkup? BuildTelegramKeyboard(List<NotificationButton>? buttons)
        {
            if (buttons == null || !buttons.Any()) return null;
            var inlineButtons = buttons.Select(b =>
                b.IsUrl
                ? InlineKeyboardButton.WithUrl(b.Text, b.CallbackDataOrUrl)
                : InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackDataOrUrl));
            return new InlineKeyboardMarkup(inlineButtons);
        }

        // The "last mile" sender. Contains only Telegram API logic and self-healing.
        private async Task SendToTelegramAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            var pollyContext = new Context($"NotificationTo_{payload.TargetTelegramUserId}");
            var finalKeyboard = BuildTelegramKeyboard(payload.Buttons);

            await _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
            {
                if (!string.IsNullOrWhiteSpace(payload.ImageUrl))
                {
                    await _botClient.SendPhoto(
                        chatId: payload.TargetTelegramUserId,
                        photo: InputFile.FromUri(payload.ImageUrl),
                        caption: payload.MessageText,
                        parseMode: ParseMode.MarkdownV2,
                        replyMarkup: finalKeyboard,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _botClient.SendMessage(
                        chatId: payload.TargetTelegramUserId,
                        text: payload.MessageText,
                        parseMode: ParseMode.MarkdownV2,
                        replyMarkup: finalKeyboard,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        cancellationToken: cancellationToken);
                }
            }, pollyContext);
            _logger.LogInformation("Notification sent successfully to User {UserId}", payload.TargetTelegramUserId);
        }


        private async Task ProcessSingleNotification(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId
            });

            try
            {
                var pollyContext = new Context($"NotificationTo_{payload.TargetTelegramUserId}");

                await _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
                {
                    // For simplicity, we assume no buttons for batch messages.
                    // This can be expanded later.
                    InlineKeyboardMarkup? finalKeyboard = null;

                    await _telegramMessageSender.SendTextMessageAsync(
                        payload.TargetTelegramUserId,
                        payload.MessageText,
                        ParseMode.MarkdownV2,
                        finalKeyboard,
                        cancellationToken,
                        new LinkPreviewOptions { IsDisabled = true });
                }, pollyContext);

                _logger.LogInformation("Message sent successfully via Telegram API.");
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || apiEx.Message.Contains("chat not found"))
            {
                _logger.LogWarning(apiEx, "Permanent Send Failure: Bot blocked or user deleted chat.");
                try
                {
                    // Self-healing logic: Mark the user as unreachable in your database.
                    await _userService.MarkUserAsUnreachableAsync(payload.TargetTelegramUserId.ToString(), "BotBlockedOrChatNotFound", CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx, "A non-critical error occurred while marking user as unreachable.");
                }
                throw; // Re-throw to fail the job.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during the final notification send.");
                throw;
            }
        }




        // The single, definitive method for building the message text.
        private string BuildMessageText(NewsItem newsItem)
        {
            var messageTextBuilder = new StringBuilder();
            string title = TelegramMessageFormatter.EscapeMarkdownV2(newsItem.Title?.Trim() ?? "Untitled News");
            string sourceName = TelegramMessageFormatter.EscapeMarkdownV2(newsItem.SourceName?.Trim() ?? "Unknown Source");
            string summary = TelegramMessageFormatter.EscapeMarkdownV2(newsItem.Summary?.Trim() ?? string.Empty);
            string? link = newsItem.Link?.Trim();

            messageTextBuilder.AppendLine($"*{title}*");
            messageTextBuilder.AppendLine($"_Source: {sourceName}_");

            if (!string.IsNullOrWhiteSpace(summary))
                messageTextBuilder.Append($"\n{summary}");

            if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out _))
                messageTextBuilder.Append($"\n\n[Read Full Article]({link})");

            return messageTextBuilder.ToString().Trim();
        }

        private List<NotificationButton> BuildSimpleNotificationButtons(NewsItem newsItem)
        {
            var buttons = new List<NotificationButton>();
            if (!string.IsNullOrWhiteSpace(newsItem.Link) && Uri.TryCreate(newsItem.Link, UriKind.Absolute, out _))
            {
                buttons.Add(new NotificationButton { Text = "Read More", CallbackDataOrUrl = newsItem.Link, IsUrl = true });
            }
            return buttons;
        }

        public Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }




        #endregion
    }
}