// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
// Project specific
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Polly;
using Polly.Retry;
using Shared.Extensions;
// Telegram.Bot
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
// TelegramPanel
using TelegramPanel.Application.CommandHandlers;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using Hangfire;
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
        [DisableConcurrentExecution(timeoutInSeconds: 1800)] // 1800 ثانیه = 30 دقیقه
        [Hangfire.JobDisplayName("Send Batch Notification ({0.Count} users) for News")]
        [Hangfire.AutomaticRetry(Attempts = 0)]
        public async Task SendBatchNotificationAsync(
            List<long> targetTelegramUserIds,
            string messageText,
            string? imageUrl,
            List<NotificationButton> buttons,
            Guid newsItemId,
            Guid? newsItemSignalCategoryId,
            string? newsItemSignalCategoryName,
            CancellationToken jobCancellationToken)
        {
            if (targetTelegramUserIds == null || !targetTelegramUserIds.Any())
            {
                _logger.LogWarning("Received an empty or null user batch to process. Skipping job.");
                return;
            }

            _logger.LogInformation(
                "Starting batch send for {UserCount} users. NewsID: {NewsItemId}",
                targetTelegramUserIds.Count, newsItemId);

            int successCount = 0;
            int failedCount = 0;

            foreach (var userId in targetTelegramUserIds)
            {
                if (jobCancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Batch processing was cancelled. Processed {ProcessedCount} users.", successCount + failedCount);
                    break;
                }

                try
                {
                    // برای هر کاربر، یک payload موقت می‌سازیم تا به متد پردازشی ارسال کنیم
                    var singlePayload = new NotificationJobPayload
                    {
                        TargetTelegramUserId = userId,
                        MessageText = messageText,
                        UseMarkdown = true, // فرض می‌کنیم همیشه Markdown است
                        ImageUrl = imageUrl,
                        Buttons = buttons,
                        NewsItemId = newsItemId,
                        NewsItemSignalCategoryId = newsItemSignalCategoryId,
                        NewsItemSignalCategoryName = newsItemSignalCategoryName,
                    };

                    await ProcessSingleNotification(singlePayload, jobCancellationToken);
                    successCount++;
                }
                catch (Exception ex)
                {
                    // ProcessSingleNotification خودش لاگ دقیق را انجام می‌دهد
                    _logger.LogError(ex, "A user failed in batch processing. UserID: {UserId}. Continuing with next.", userId);
                    failedCount++;
                }

                // ----> مهم‌ترین بخش: اعمال تاخیر <----
                await Task.Delay(DelayBetweenBatchMessagesMs, jobCancellationToken);
            }

            _logger.LogInformation(
                "Finished batch send for NewsID: {NewsItemId}. Success: {SuccessCount}, Failed: {FailedCount}.",
                newsItemId, successCount, failedCount);
        }

        #endregion

        #region Private Helper Methods

        // ✅✅ [تغییر] ✅✅
        // منطق اصلی ارسال به این متد خصوصی منتقل شده تا از تکرار کد جلوگیری شود.
        private async Task ProcessSingleNotification(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId,
                ["NewsItemId"] = payload.NewsItemId
            });

            try
            {
                var userDto = await _userService.GetUserByTelegramIdAsync(payload.TargetTelegramUserId.ToString(), cancellationToken);
                if (userDto == null)
                {
                    _logger.LogWarning("User not found, skipping.");
                    return; // کاربر دیگر وجود ندارد
                }

                // ساخت دکمه‌ها بر اساس منطق دقیق شما
                var inlineButtons = new List<InlineKeyboardButton>();
                if (payload.Buttons != null)
                {
                    var readMoreButton = payload.Buttons.FirstOrDefault(b => b.IsUrl);
                    if (readMoreButton != null)
                    {
                        inlineButtons.Add(InlineKeyboardButton.WithUrl(TelegramMessageFormatter.EscapeMarkdownV2(readMoreButton.Text), readMoreButton.CallbackDataOrUrl));
                    }
                }
                if (payload.NewsItemSignalCategoryId.HasValue && !string.IsNullOrWhiteSpace(payload.NewsItemSignalCategoryName))
                {
                    bool isSubscribed = await _userPrefsRepository.IsUserSubscribedToCategoryAsync(userDto.Id, payload.NewsItemSignalCategoryId.Value, cancellationToken);
                    inlineButtons.Add(isSubscribed
                        ? InlineKeyboardButton.WithCallbackData($"✅ Unsubscribe from {payload.NewsItemSignalCategoryName.Truncate(20)}", $"{NewsNotificationCallbackHandler.UnsubscribeFromCategoryPrefix}{payload.NewsItemSignalCategoryId.Value}")
                        : InlineKeyboardButton.WithCallbackData($"➕ Subscribe to {payload.NewsItemSignalCategoryName.Truncate(20)}", $"{NewsNotificationCallbackHandler.SubscribeToCategoryPrefix}{payload.NewsItemSignalCategoryId.Value}"));
                }
                InlineKeyboardMarkup? finalKeyboard = inlineButtons.Any() ? new InlineKeyboardMarkup(inlineButtons) : null;

                var pollyContext = new Context($"NotificationTo_{payload.TargetTelegramUserId}", new Dictionary<string, object> { { "TelegramUserId", payload.TargetTelegramUserId } });

                // ارسال پیام
                if (!string.IsNullOrWhiteSpace(payload.ImageUrl))
                {
                    await _telegramApiRetryPolicy.ExecuteAsync(ctx => _telegramMessageSender.SendPhotoAsync(
                        payload.TargetTelegramUserId, payload.ImageUrl, payload.MessageText, ParseMode.MarkdownV2, finalKeyboard, cancellationToken), pollyContext);
                }
                else
                {
                    await _telegramApiRetryPolicy.ExecuteAsync(ctx => _telegramMessageSender.SendTextMessageAsync(
                        payload.TargetTelegramUserId, payload.MessageText, ParseMode.MarkdownV2, finalKeyboard, cancellationToken, new LinkPreviewOptions { IsDisabled = true }), pollyContext);
                }

                _logger.LogInformation("Notification successfully sent.");
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || (apiEx.Message != null && apiEx.Message.Contains("chat not found")))
            {
                _logger.LogWarning(apiEx, "Non-retryable error (bot blocked or user deleted). User will be marked as unreachable.");
                // await _userService.MarkUserAsUnreachableAsync(payload.TargetTelegramUserId.ToString(), "BotBlockedOrChatNotFound", cancellationToken);
                throw; // این خطا باعث fail شدن جاب تکی می‌شود و در جاب دسته‌ای، catch می‌شود.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Operation was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during notification processing.");
                throw;
            }
        }

        private static bool ShouldRetryTelegramApiException(ApiRequestException ex)
        {
            if (ex.ErrorCode == 429 || ex.Parameters?.RetryAfter.HasValue == true) return true;
            if (ex.ErrorCode >= 500 && ex.ErrorCode < 600) return true;
            return false;
        }
        #endregion
    }
}