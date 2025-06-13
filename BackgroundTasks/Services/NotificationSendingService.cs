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
using StackExchange.Redis;
using System.Text;
using System.Text.Json;
// Telegram.Bot
using Telegram.Bot.Exceptions;
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

        private const int RegularUserHourlyLimit = 5;
        private const int VipUserHourlyLimit = 20;
        private static readonly TimeSpan LimitPeriod = TimeSpan.FromHours(1);
        #endregion

        #region Constructor
        public NotificationSendingService(
            ILogger<NotificationSendingService> logger,
            ITelegramMessageSender telegramMessageSender,
            IUserService userService,
            INewsItemRepository newsItemRepository,
            IConnectionMultiplexer redisConnection,
            INotificationRateLimiter rateLimiter)
        {
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

        /// <summary>
        /// The primary Hangfire job method. Implements safeguards before sending a notification.
        /// </summary>
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        [JobDisplayName("Process Notification (Secure): News {0}, UserIndex {2}")]
        [AutomaticRetry(Attempts = 0)]
        public async Task ProcessNotificationFromCacheAsync(Guid newsItemId, string userListCacheKey, int userIndex)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?> { ["NewsItemId"] = newsItemId, ["UserIndex"] = userIndex });

            try
            {
                long targetUserId = await GetUserIdFromCacheAsync(userListCacheKey, userIndex);
                if (targetUserId == -1) return;

                _logger.LogInformation("Processing job for UserID: {UserId}", targetUserId);

                var userDto = await _userService.GetUserByTelegramIdAsync(targetUserId.ToString(), CancellationToken.None);
                if (userDto == null)
                {
                    _logger.LogWarning("User {UserId} not found in database. Skipping.", targetUserId);
                    return;
                }

                // Corrected UserLevel check
                int limit = userDto.Level == UserLevel.Bronze ? VipUserHourlyLimit : RegularUserHourlyLimit;
                if (await _rateLimiter.IsUserOverLimitAsync(targetUserId, limit, LimitPeriod))
                {
                    _logger.LogWarning("FAST SKIP: User {UserId} (Level: {UserLevel}) is over the hourly limit of {Limit}.",
                        targetUserId, userDto.Level, limit);
                    return;
                }

                var newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, CancellationToken.None);
                if (newsItem == null) throw new InvalidOperationException($"NewsItem {newsItemId} not found.");

                var payload = new NotificationJobPayload
                {
                    TargetTelegramUserId = targetUserId,
                    MessageText = BuildMessageText(newsItem), // Call the single, clean helper
                    UseMarkdown = true,
                    ImageUrl = newsItem.ImageUrl,
                    Buttons = BuildSimpleNotificationButtons(newsItem),
                    NewsItemId = newsItemId
                };

                await SendToTelegramAsync(payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical failure occurred during a cached notification job.");
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

        // The "last mile" sender. Contains only Telegram API logic and self-healing.
        private async Task SendToTelegramAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async () =>
                {
                    var finalKeyboard = BuildTelegramKeyboard(payload.Buttons);
                    var parseMode = payload.UseMarkdown ? ParseMode.MarkdownV2 : ParseMode.Html;
                    if (!string.IsNullOrWhiteSpace(payload.ImageUrl))
                        await _telegramMessageSender.SendPhotoAsync(payload.TargetTelegramUserId, payload.ImageUrl, payload.MessageText, parseMode, finalKeyboard, cancellationToken);
                    else
                        await _telegramMessageSender.SendTextMessageAsync(payload.TargetTelegramUserId, payload.MessageText, parseMode, finalKeyboard, cancellationToken);
                });
                _logger.LogInformation("Notification successfully sent to UserID {UserId}.", payload.TargetTelegramUserId);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || apiEx.Message.Contains("chat not found"))
            {

                throw; // Re-throw to fail the job in Hangfire
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during the final send to UserID {UserId}.", payload.TargetTelegramUserId);
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

        private InlineKeyboardMarkup? BuildTelegramKeyboard(List<NotificationButton>? buttons)
        {
            if (buttons == null || !buttons.Any()) return null;
            var inlineButtons = buttons.Select(b =>
                b.IsUrl
                ? InlineKeyboardButton.WithUrl(b.Text, b.CallbackDataOrUrl)
                : InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackDataOrUrl));
            return new InlineKeyboardMarkup(inlineButtons);
        }

        public Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task SendBatchNotificationAsync(List<long> targetTelegramUserIds, string messageText, string? imageUrl, List<NotificationButton> buttons, Guid newsItemId, Guid? newsItemSignalCategoryId, string? newsItemSignalCategoryName, CancellationToken jobCancellationToken)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}