// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Hangfire;
using Polly;
using Polly.Retry;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;

// Telegram.Bot
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
// TelegramPanel
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
#endregion

namespace BackgroundTasks.Services
{
    public class NotificationSendingService : INotificationSendingService
    {
        #region Dependencies
        private readonly ILogger<NotificationSendingService> _logger;
        private readonly ITelegramMessageSender _telegramMessageSender;
        private readonly IUserService _userService;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly IDatabase _redisDb;
        private readonly INotificationRateLimiter _rateLimiter;
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy; // ✅ DECLARED HERE
        #endregion

        #region Constructor
        // ✅✅ THIS IS THE CORRECT CONSTRUCTOR FOR THIS CLASS ✅✅
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

            // ✅ INITIALIZED HERE
            _telegramApiRetryPolicy = Policy
                .Handle<ApiRequestException>(ex => ex.ErrorCode == 429)
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
        }
        #endregion

        #region INotificationSendingService Implementation






        [DisableConcurrentExecution(timeoutInSeconds: 600)] // 10 minute timeout per job
        [Hangfire.JobDisplayName("Process Notification (Cached) for News: {0}, UserIndex: {2}")]
        [Hangfire.AutomaticRetry(Attempts = 0)] // Do not retry send operations
        public async Task ProcessNotificationFromCacheAsync(Guid newsItemId, string userListCacheKey, int userIndex)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["NewsItemId"] = newsItemId,
                ["UserListCacheKey"] = userListCacheKey,
                ["UserIndex"] = userIndex
            });

            try
            {
                var serializedUserIds = await _redisDb.StringGetAsync(userListCacheKey);
                if (!serializedUserIds.HasValue)
                {
                    throw new InvalidOperationException($"Cache key {userListCacheKey} not found. Job cannot proceed.");
                }

                var allUserIds = JsonSerializer.Deserialize<List<long>>(serializedUserIds);

                if (allUserIds == null || userIndex >= allUserIds.Count)
                {
                    throw new IndexOutOfRangeException("User index is out of bounds for the cached user list.");
                }
                long targetUserId = allUserIds[userIndex];

                _logger.LogInformation("Processing job for UserID: {UserId} from cached list.", targetUserId);

                // This rate limit check is a final validation inside the job.
                // The dispatcher already does a pre-flight check, but this ensures safety
                // if jobs are delayed and run in a different time window.
                if (await _rateLimiter.IsUserOverLimitAsync(targetUserId, 10, TimeSpan.FromHours(1)))
                {
                    _logger.LogWarning("FAST SKIP inside job: User {UserId} is over the hourly limit. Job complete.", targetUserId);
                    return;
                }

                var newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, CancellationToken.None);
                if (newsItem == null)
                {
                    throw new InvalidOperationException($"NewsItem {newsItemId} not found.");
                }

                var payload = new NotificationJobPayload
                {
                    TargetTelegramUserId = targetUserId,
                    MessageText = BuildMessageText(newsItem),
                    UseMarkdown = true,
                    ImageUrl = newsItem.ImageUrl,
                    // We build the simple buttons here. No need for user preferences.
                    Buttons = BuildSimpleNotificationButtons(newsItem),
                    NewsItemId = newsItemId
                };

                await ProcessSingleNotification(payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical failure occurred during the execution of a cached notification job.");
                throw; // Re-throw to ensure Hangfire marks the job as "Failed".
            }
        }
   






        // متد ارسال تکی شما دست‌نخورده باقی می‌ماند
        [Hangfire.JobDisplayName("Send Single Notification to: {0.TargetTelegramUserId}")]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken jobCancellationToken)
        {
            // منطق قبلی شما در این متد قرار دارد
            // این کد را برای خوانایی به یک متد خصوصی منتقل می‌کنیم تا در هر دو حالت از آن استفاده کنیم.
            await ProcessSingleNotification(payload, jobCancellationToken);
        }

        // ✅✅ [جدید] ✅✅
        // متد جدید برای ارسال دسته‌ای بدون نیاز به DTO جدید.
        // توجه: این متد باید به اینترفیس INotificationSendingService هم اضافه شود.
        [DisableConcurrentExecution(timeoutInSeconds: 600)] // 600 seconds = 10 minutes
                                                            // The display name still works perfectly, as it will now show "(1 users)".
        [Hangfire.JobDisplayName("Send Notification to UserID: {0[0]} for News: {4}")]
        // IMPORTANT: Do NOT retry notification jobs. If a user has blocked the bot or the send fails
        // for a specific reason, retrying could be seen as spam and won't fix the underlying issue.
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task SendBatchNotificationAsync(
      List<long> targetTelegramUserIdList, // Renamed to reflect it's a list, though usually with one item
      string messageText,
      string? imageUrl,
      List<NotificationButton> buttons,
      Guid newsItemId,
      Guid? newsItemSignalCategoryId,
      string? newsItemSignalCategoryName,
      CancellationToken jobCancellationToken)
        {
            // --- 1. Validation ---
            // Although the dispatcher sends one user, we defend against empty/null lists.
            if (targetTelegramUserIdList == null || !targetTelegramUserIdList.Any())
            {
                _logger.LogWarning("Job received an empty or null user list. Skipping.");
                return;
            }

            // Since the dispatcher now sends one user per job, we process only the first.
            var userId = targetTelegramUserIdList[0];

            _logger.LogInformation(
                "Executing notification job for UserID: {UserId}, NewsID: {NewsItemId}",
                userId, newsItemId);

            try
            {
                // --- 2. Execution ---
                // Create the payload for the single user this job is responsible for.
                var singlePayload = new NotificationJobPayload
                {
                    TargetTelegramUserId = userId,
                    MessageText = messageText,
                    UseMarkdown = true,
                    ImageUrl = imageUrl,
                    Buttons = buttons,
                    NewsItemId = newsItemId,
                    NewsItemSignalCategoryId = newsItemSignalCategoryId,
                    NewsItemSignalCategoryName = newsItemSignalCategoryName,
                };

                // Process the single notification. This is the core work of the job.
                await ProcessSingleNotification(singlePayload, jobCancellationToken);

                _logger.LogInformation(
                    "Successfully completed notification job for UserID: {UserId}, NewsID: {NewsItemId}",
                    userId, newsItemId);
            }
            catch (OperationCanceledException)
            {
                // This will trigger if the job is cancelled from the Hangfire dashboard or if its timeout is reached.
                _logger.LogWarning("Notification job for UserID {UserId} was cancelled.", userId);
                // Re-throw so Hangfire correctly marks the job as Canceled/Failed instead of Succeeded.
                throw;
            }
            catch (Exception ex)
            {
                // The ProcessSingleNotification method should handle detailed error logging (e.g., user blocked bot).
                // This catch is the final safety net.
                _logger.LogCritical(ex, "A critical, unhandled exception occurred in the notification job for UserID {UserId}.", userId);
                // Re-throw so Hangfire marks the job as Failed.
                throw;
            }
        }
        #endregion

        #region Private Helper Methods
        // This helper builds the simple message text.
        private string BuildMessageText(NewsItem newsItem)
        {
            var messageTextBuilder = new StringBuilder();
            string title = newsItem.Title?.Trim() ?? "Untitled News";
            _ = messageTextBuilder.AppendLine($"*{TelegramMessageFormatter.EscapeMarkdownV2(title)}*");
            // Add other parts like summary, etc., as needed.
            return messageTextBuilder.ToString().Trim();
        }

        // This helper builds the simple, static buttons. No DB call needed.
        private List<NotificationButton> BuildSimpleNotificationButtons(NewsItem newsItem)
        {
            var buttons = new List<NotificationButton>();
            if (!string.IsNullOrWhiteSpace(newsItem.Link) && Uri.TryCreate(newsItem.Link, UriKind.Absolute, out _))
            {
                buttons.Add(new NotificationButton { Text = "Read More", CallbackDataOrUrl = newsItem.Link, IsUrl = true });
            }
            // You can add other static buttons here if needed.
            return buttons;
        }

        // This helper converts your DTO to the Telegram.Bot library's format.
        private InlineKeyboardMarkup? BuildTelegramKeyboard(List<NotificationButton>? buttons)
        {
            if (buttons == null || !buttons.Any()) return null;

            var inlineButtons = buttons.Select(b =>
                b.IsUrl
                ? InlineKeyboardButton.WithUrl(b.Text, b.CallbackDataOrUrl)
                : InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackDataOrUrl)
            );

            // This assumes one button per row. You can add more complex logic to group them.
            return new InlineKeyboardMarkup(inlineButtons);
        }

        // ✅✅ [تغییر] ✅✅
        // منطق اصلی ارسال به این متد خصوصی منتقل شده تا از تکرار کد جلوگیری شود.
        /// <summary>
        /// The core processing unit for a single notification. This method is a self-contained,
        /// fortified algorithm with integrated timeouts ("Hang Shield") and self-healing logic.
        /// </summary>
        // Make sure INotificationRateLimiter is injected into your service class's constructor
        // and assigned to a field, for example: `private readonly INotificationRateLimiter _rateLimiter;`

        /// <summary>
        /// The core processing unit for a single notification. This method includes a high-speed
        /// rate-limiting check and is fortified with timeouts ("Hang Shield") and self-healing logic.
        /// </summary>
           // This is the simplified "last mile" sender. It ONLY talks to the Telegram API.
        private async Task ProcessSingleNotification(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId,
                ["NewsItemId"] = payload.NewsItemId
            });

            try
            {
                var pollyContext = new Context($"NotificationTo_{payload.TargetTelegramUserId}");

                using var operationTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationTimeoutCts.Token);

                await _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
                {
                    var finalKeyboard = BuildTelegramKeyboard(payload.Buttons);

                    if (!string.IsNullOrWhiteSpace(payload.ImageUrl))
                    {
                        await _telegramMessageSender.SendPhotoAsync(
                            payload.TargetTelegramUserId, payload.ImageUrl, payload.MessageText, ParseMode.MarkdownV2, finalKeyboard, linkedCts.Token);
                    }
                    else
                    {
                        await _telegramMessageSender.SendTextMessageAsync(
                            payload.TargetTelegramUserId, payload.MessageText, ParseMode.MarkdownV2, finalKeyboard, linkedCts.Token, new LinkPreviewOptions { IsDisabled = true });
                    }
                }, pollyContext);

                _logger.LogInformation("Successfully sent notification via Telegram API.");
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || apiEx.Message.Contains("chat not found"))
            {
                _logger.LogWarning(apiEx, "Permanent Send Failure: Bot blocked or chat deleted.");
             
                throw; // Fail the job.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Operation was cancelled by Hangfire.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during the final notification send.");
                throw;
            }
        }

        private static bool ShouldRetryTelegramApiException(ApiRequestException ex)
        {
            return ex.ErrorCode == 429 || ex.Parameters?.RetryAfter.HasValue == true ? true : ex.ErrorCode is >= 500 and < 600;
        }
        #endregion
    }
}