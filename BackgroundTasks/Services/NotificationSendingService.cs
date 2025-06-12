// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces;
using Application.DTOs;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Hangfire;
using Polly;
using Polly.Retry;
using Shared.Extensions;
// Telegram.Bot
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.Features.News;
// TelegramPanel
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
#endregion

namespace BackgroundTasks.Services
{
    public class NotificationSendingService : INotificationSendingService
    {
        #region Private Readonly Fields
        private readonly ITelegramMessageSender _telegramMessageSender;
        private readonly IUserService _userService;
        private readonly ILogger<NotificationSendingService> _logger;
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy;
        private readonly IUserSignalPreferenceRepository _userPrefsRepository;
        #endregion

        #region Constants
        private const int MaxRetries = 3;
        // ✅✅ [جدید] ✅✅: ثابت برای کنترل نرخ ارسال در حالت دسته‌ای
        private const int DelayBetweenBatchMessagesMs = 5; // ~25 پیام در ثانیه
        #endregion

        #region Constructor
        public NotificationSendingService(
            ITelegramMessageSender telegramMessageSender,
            IUserService userService,
            IUserSignalPreferenceRepository userPrefsRepository,
            IAppDbContext appDbContext,
            ILogger<NotificationSendingService> logger)
        {
            _userPrefsRepository = userPrefsRepository ?? throw new ArgumentNullException(nameof(userPrefsRepository));
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // Polly Policy definition remains unchanged...
            _telegramApiRetryPolicy = Policy
                .Handle<ApiRequestException>(ex => ShouldRetryTelegramApiException(ex))
                .Or<HttpRequestException>()
                .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                .WaitAndRetryAsync(
                    retryCount: MaxRetries,
                    sleepDurationProvider: (retryAttempt, exception, context) =>
                    {
                        TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                        object? telegramUserIdObj = context.Contains("TelegramUserId") ? context["TelegramUserId"] : "N/A";

                        if (exception is ApiRequestException apiEx && apiEx.Parameters?.RetryAfter.HasValue == true)
                        {
                            delay = TimeSpan.FromSeconds(apiEx.Parameters.RetryAfter.Value + 1); // +1 to be safe
                            _logger.LogWarning("Telegram API rate limit hit for UserID {TelegramUserId}. Retrying after {DelaySeconds:F1}s.", telegramUserIdObj, delay.TotalSeconds);
                        }
                        else
                        {
                            _logger.LogWarning(exception, "Transient error for UserID {TelegramUserId}. Retrying in {DelaySeconds:F1}s.", telegramUserIdObj, delay.TotalSeconds);
                        }
                        return delay;
                    },
                    onRetryAsync: (exception, timespan, retryAttempt, context) => Task.CompletedTask);
        }
        #endregion

        #region INotificationSendingService Implementation

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

        // ✅✅ [تغییر] ✅✅
        // منطق اصلی ارسال به این متد خصوصی منتقل شده تا از تکرار کد جلوگیری شود.
        /// <summary>
        /// The core processing unit for a single notification. This method is a self-contained,
        /// fortified algorithm with integrated timeouts ("Hang Shield") and self-healing logic.
        /// </summary>
        private async Task ProcessSingleNotification(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            // --- Configuration is now defined directly inside the method ---
            var operationTimeout = TimeSpan.FromSeconds(15);

            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId,
                ["NewsItemId"] = payload.NewsItemId
            });

            try
            {
                // --- 1. Fetch User Data (with In-Method Hang Shield) ---
                UserDto? userDto;
                var getUserTask = _userService.GetUserByTelegramIdAsync(payload.TargetTelegramUserId.ToString(), cancellationToken);
                if (await Task.WhenAny(getUserTask, Task.Delay(operationTimeout, cancellationToken)) == getUserTask)
                {
                    userDto = await getUserTask;
                }
                else
                {
                    throw new TimeoutException($"Operation 'GetUserByTelegramId' timed out after {operationTimeout.TotalSeconds} seconds.");
                }

                if (userDto == null)
                {
                    _logger.LogWarning("User not found in database, skipping notification.");
                    return; // The user no longer exists, job is successful (nothing to do).
                }

                // --- 2. Build Keyboard (with In-Method Hang Shield) ---
                InlineKeyboardMarkup? finalKeyboard;
                var buildKeyboardTask = Task.Run(async () => // Encapsulate the entire keyboard logic in a Task
                {
                    var inlineButtons = new List<InlineKeyboardButton>();

                    // "Read More" button
                    var readMoreButton = payload.Buttons?.FirstOrDefault(b => b.IsUrl);
                    if (readMoreButton != null)
                    {
                        inlineButtons.Add(InlineKeyboardButton.WithUrl(TelegramMessageFormatter.EscapeMarkdownV2(readMoreButton.Text), readMoreButton.CallbackDataOrUrl));
                    }

                    // "Subscribe/Unsubscribe" button (includes a database call)
                    if (payload.NewsItemSignalCategoryId.HasValue && !string.IsNullOrWhiteSpace(payload.NewsItemSignalCategoryName))
                    {
                        // The DB call is now inside this task, so it's protected by the timeout below.
                        bool isSubscribed = await _userPrefsRepository.IsUserSubscribedToCategoryAsync(userDto.Id, payload.NewsItemSignalCategoryId.Value, cancellationToken);
                        inlineButtons.Add(isSubscribed
                            ? InlineKeyboardButton.WithCallbackData($"✅ Unsubscribe from {payload.NewsItemSignalCategoryName.Truncate(20)}", $"{NewsNotificationCallbackHandler.UnsubscribeFromCategoryPrefix}{payload.NewsItemSignalCategoryId.Value}")
                            : InlineKeyboardButton.WithCallbackData($"➕ Subscribe to {payload.NewsItemSignalCategoryName.Truncate(20)}", $"{NewsNotificationCallbackHandler.SubscribeToCategoryPrefix}{payload.NewsItemSignalCategoryId.Value}"));
                    }

                    return inlineButtons.Any() ? new InlineKeyboardMarkup(inlineButtons) : null;
                }, cancellationToken);

                if (await Task.WhenAny(buildKeyboardTask, Task.Delay(operationTimeout, cancellationToken)) == buildKeyboardTask)
                {
                    finalKeyboard = await buildKeyboardTask;
                }
                else
                {
                    throw new TimeoutException($"Operation 'BuildKeyboard' timed out after {operationTimeout.TotalSeconds} seconds.");
                }

                // --- 3. Send to Telegram (with In-Method Hang Shield + Polly Retries) ---
                var pollyContext = new Context($"NotificationTo_{payload.TargetTelegramUserId}");

                var sendWithPollyTask = _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
                {
                    if (!string.IsNullOrWhiteSpace(payload.ImageUrl))
                    {
                        await _telegramMessageSender.SendPhotoAsync(
                            payload.TargetTelegramUserId, payload.ImageUrl, payload.MessageText, ParseMode.MarkdownV2, finalKeyboard, cancellationToken);
                    }
                    else
                    {
                        await _telegramMessageSender.SendTextMessageAsync(
                            payload.TargetTelegramUserId, payload.MessageText, ParseMode.MarkdownV2, finalKeyboard, cancellationToken, new LinkPreviewOptions { IsDisabled = true });
                    }
                }, pollyContext);

                if (await Task.WhenAny(sendWithPollyTask, Task.Delay(operationTimeout, cancellationToken)) != sendWithPollyTask)
                {
                    throw new TimeoutException($"Operation 'SendTelegramMessageWithPolly' timed out after {operationTimeout.TotalSeconds} seconds.");
                }
                await sendWithPollyTask; // Await again to propagate any exceptions from Polly itself.


                _logger.LogInformation("Notification successfully sent.");
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || (apiEx.Message != null && apiEx.Message.Contains("chat not found")))
            {
                _logger.LogWarning(apiEx, "Permanent Send Failure: Bot was blocked or user deleted chat.");

                // --- 4. Self-Healing Logic (In-Method) ---
                try
                {
                    // This "fire and forget" call is protected from bringing down the main logic.
                    // We don't want its failure to hide the real reason the job failed (ApiRequestException).
                    _logger.LogInformation("Successfully marked user as unreachable.");
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx, "A non-critical error occurred while marking user as unreachable.");
                }

                // Re-throw the original, important exception to fail the Hangfire job correctly.
                throw;
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, "An operation timed out, preventing the worker from hanging.");
                throw; // Fail the job.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Operation was cancelled by Hangfire.");
                throw; // Fail the job.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during notification processing.");
                throw; // Fail the job.
            }
        }

        private static bool ShouldRetryTelegramApiException(ApiRequestException ex)
        {
            return ex.ErrorCode == 429 || ex.Parameters?.RetryAfter.HasValue == true ? true : ex.ErrorCode is >= 500 and < 600;
        }
        #endregion
    }
}