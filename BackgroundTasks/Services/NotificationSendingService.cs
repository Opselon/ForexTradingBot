// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http; // برای HttpRequestException
using System.Threading;
using System.Threading.Tasks;

// Telegram.Bot
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;         // ✅✅ برای ParseMode ✅✅
using Telegram.Bot.Types.ReplyMarkups; // برای InlineKeyboardMarkup, InlineKeyboardButton

// Project specific
using Application.Common.Interfaces;    // برای INotificationSendingService (اینترفیسی که این کلاس پیاده‌سازی می‌کند)
using Application.DTOs.Notifications;   // برای NotificationJobPayload, NotificationButton
using Application.Interfaces;
using Shared.Extensions;
using Telegram.Bot.Types;
using TelegramPanel.Application.CommandHandlers; // ✅ برای IUserService (از پروژه Application اصلی)

// ✅✅ Using های مربوط به TelegramPanel (نیاز به ارجاع پروژه BackgroundTasks به TelegramPanel) ✅✅
using TelegramPanel.Formatters;         // ✅ برای TelegramMessageFormatter
using TelegramPanel.Infrastructure;     // ✅ برای ITelegramMessageSender
#endregion

namespace BackgroundTasks.Services
{
    /// <summary>
    /// Handles the actual sending of notifications to users via Telegram.
    /// This service is designed to be invoked by a background job system like Hangfire.
    /// It incorporates robust error handling, retry mechanisms (Polly), and adheres to Telegram API rate limits.
    /// </summary>
    public class NotificationSendingService : INotificationSendingService
    {
        #region Private Readonly Fields
        private readonly ITelegramMessageSender _telegramMessageSender; // از TelegramPanel.Infrastructure
        private readonly IUserService _userService;                 // از Core Application layer
        private readonly ILogger<NotificationSendingService> _logger;
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy;
        private readonly IUserSignalPreferenceRepository _userPrefsRepository; // ✅ اضافه شد
        private readonly IAppDbContext _appDbContext; // ✅ برای خواندن User Entity کامل (اگر لازم باشد)
        #endregion

        #region Constants
        private const int MaxRetries = 3;
        #endregion

        #region Constructor
        public NotificationSendingService(
            ITelegramMessageSender telegramMessageSender, // ✅ از TelegramPanel.Infrastructure
            IUserService userService,
            IUserSignalPreferenceRepository userPrefsRepository, // ✅ اضافه شد
            IAppDbContext appDbContext, // ✅ از Application.Interfaces
            ILogger<NotificationSendingService> logger)
        {
            _userPrefsRepository = userPrefsRepository ?? throw new ArgumentNullException(nameof(userPrefsRepository));
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appDbContext = appDbContext ?? throw new ArgumentNullException(nameof(appDbContext));
            // Define a Polly retry policy
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
                        object? jobPayloadObj = context.Contains("JobPayload") ? context["JobPayload"] : null;

                        if (exception is ApiRequestException apiEx && apiEx.Parameters?.RetryAfter.HasValue == true)
                        {
                            delay = TimeSpan.FromSeconds(apiEx.Parameters.RetryAfter.Value + new Random().Next(1, 3));
                            _logger.LogWarning(
                                "Telegram API rate limit hit for UserID {TelegramUserId}. RetryAttempt: {RetryAttempt}. Retrying after {DelaySeconds:F1}s (from API). JobPayload Hash: {JobPayloadHash}",
                                telegramUserIdObj, retryAttempt, delay.TotalSeconds, jobPayloadObj?.GetHashCode()); // لاگ کردن هش Payload برای جلوگیری از لاگ کردن اطلاعات حساس
                        }
                        else
                        {
                            _logger.LogWarning(exception,
                                "Transient error sending notification to UserID {TelegramUserId}. RetryAttempt: {RetryAttempt}. Retrying in {DelaySeconds:F1}s. JobPayload Hash: {JobPayloadHash}",
                                telegramUserIdObj, retryAttempt, delay.TotalSeconds, jobPayloadObj?.GetHashCode());
                        }
                        return delay;
                    },
                    onRetryAsync: (exception, timespan, retryAttempt, context) =>
                    {
                        object? telegramUserIdObj = context.Contains("TelegramUserId") ? context["TelegramUserId"] : "N/A";
                        _logger.LogInformation(
                            "Retrying notification for UserID {TelegramUserId} (Attempt {RetryAttempt} of {MaxRetriesCount}) after delay of {DelaySeconds:F1}s due to {ExceptionType}.",
                            telegramUserIdObj, retryAttempt, MaxRetries, timespan.TotalSeconds, exception.GetType().Name);
                        return Task.CompletedTask;
                    });
        }
        #endregion

        #region INotificationSendingService Implementation
        [Hangfire.JobDisplayName("Send Telegram Notification to User: {0.TargetTelegramUserId}")]
        [Hangfire.AutomaticRetry(Attempts = 0)] // We use Polly for retries
        public async Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken jobCancellationToken)
        {
            if (payload == null)
            {
                _logger.LogError("SendNotificationAsync job received a null payload. Job cannot be processed.");
                throw new ArgumentNullException(nameof(payload), "NotificationJobPayload cannot be null.");
            }
            var logScope = new Dictionary<string, object?>
            {
                ["JobType"] = "NewsNotification",
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId,
                ["NewsItemId"] = payload.NewsItemId,
                ["NewsItemSignalCategoryId"] = payload.NewsItemSignalCategoryId
            };
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["JobType"] = "TelegramNotification",
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId,
                ["RelatedNewsImageUrl"] = payload.ImageUrl,
                ["NotificationMessageHash"] = payload.MessageText?.GetHashCode() // لاگ کردن هش پیام
            }))
            {
                _logger.LogInformation("Starting to process news notification job.");

                try
                {
                    // (اختیاری) بررسی اینکه آیا کاربر هنوز می‌خواهد این نوع نوتیفیکیشن را دریافت کند
                    // این نیاز به دسترسی به User Entity یا UserDto با تنظیمات نوتیفیکیشن دارد
                    // var user = await _userService.GetUserByTelegramIdAsync(payload.TargetTelegramUserId.ToString(), jobCancellationToken);
                    // if (user == null || !user.WantsThisTypeOfNotification) // 'WantsThisTypeOfNotification' یک فیلد فرضی است
                    // {
                    //     _logger.LogInformation("User {TelegramUserId} no longer exists or has disabled this type of notification. Skipping.", payload.TargetTelegramUserId);
                    //     return;
                    // }

                    string messageTextToSend = payload.MessageText;
                    ParseMode? parseMode = payload.UseMarkdown ? ParseMode.MarkdownV2 : null;

                    // ✅ اگر از MarkdownV2 استفاده می‌کنید، مطمئن شوید متن به درستی escape شده است.
                    // این کار باید یا در NotificationDispatchService هنگام ساخت MessageText انجام شده باشد،
                    // یا TelegramMessageFormatter باید اینجا فراخوانی شود.
                    if (parseMode == ParseMode.MarkdownV2 && !string.IsNullOrEmpty(messageTextToSend))
                    {
                        // فرض می‌کنیم TelegramMessageFormatter.EscapeMarkdownV2 متن خام را می‌گیرد و escape می‌کند.
                        // اگر messageTextToSend از قبل شامل کاراکترهای فرمت Markdown است، نباید دوباره escape شود.
                        // این بستگی به منطق NotificationDispatchService دارد.
                        // برای اطمینان، اگر پیام از قبل فرمت شده، escape نکنید.
                        // فعلاً فرض می‌کنیم متن خام است و نیاز به escape توسط فرمتتر دارد.
                        // messageTextToSend = TelegramMessageFormatter.EscapeMarkdownV2(payload.MessageText);
                        // یا اگر فرمتترهای Bold, Italic و ... خودشان escape می‌کنند، نیازی به این خط نیست.
                        // این بخش نیاز به بررسی دقیق جریان داده شما دارد.
                    }

                    InlineKeyboardMarkup? inlineKeyboard = null;
                    if (payload.Buttons != null && payload.Buttons.Any())
                    {
                        var keyboardButtonRows = payload.Buttons
                            .Select(b => new[] { b.IsUrl
                                ? InlineKeyboardButton.WithUrl(TelegramMessageFormatter.EscapeMarkdownV2(b.Text), b.CallbackDataOrUrl) // متن دکمه هم ممکن است نیاز به escape داشته باشد
                                : InlineKeyboardButton.WithCallbackData(TelegramMessageFormatter.EscapeMarkdownV2(b.Text), b.CallbackDataOrUrl) })
                            .ToList();
                        if (keyboardButtonRows.Any()) inlineKeyboard = new InlineKeyboardMarkup(keyboardButtonRows);
                    }

                    var pollyContext = new Context($"NotificationTo_{payload.TargetTelegramUserId}")
                    {
                        { "TelegramUserId", payload.TargetTelegramUserId },
                        { "JobPayload", payload.GetHashCode() } //  فقط هش برای لاگ
                    };

                    var userDto = await _userService.GetUserByTelegramIdAsync(payload.TargetTelegramUserId.ToString(), jobCancellationToken);
                    if (userDto == null)
                    {
                        _logger.LogWarning("User with TelegramID {TelegramUserId} not found. Aborting notification for NewsItemID {NewsItemId}.",
                            payload.TargetTelegramUserId, payload.NewsItemId);
                        return;
                    }
                    Guid systemUserId = userDto.Id;

                    var inlineButtons = new List<InlineKeyboardButton>();

                    // دکمه Read More (همیشه وجود دارد)
                    if (payload.Buttons != null && payload.Buttons.Any(b => b.IsUrl && b.Text.Contains("Read More", StringComparison.OrdinalIgnoreCase)))
                    {
                        var readMoreButton = payload.Buttons.First(b => b.IsUrl && b.Text.Contains("Read More"));
                        inlineButtons.Add(InlineKeyboardButton.WithUrl(
                            TelegramMessageFormatter.EscapeMarkdownV2(readMoreButton.Text), //  متن دکمه هم باید escape شود
                            readMoreButton.CallbackDataOrUrl));
                    }
                    if (payload.NewsItemSignalCategoryId.HasValue && !string.IsNullOrWhiteSpace(payload.NewsItemSignalCategoryName))
                    {
                        bool isSubscribedToCategory = await _userPrefsRepository.IsUserSubscribedToCategoryAsync(systemUserId, payload.NewsItemSignalCategoryId.Value, jobCancellationToken);

                        if (isSubscribedToCategory)
                        {
                            inlineButtons.Add(InlineKeyboardButton.WithCallbackData(
                                $"✅ Unsubscribe from {TelegramMessageFormatter.EscapeMarkdownV2(payload.NewsItemSignalCategoryName.Truncate(20))}", //  متن دکمه را کوتاه نگه دارید
                                $"{NewsNotificationCallbackHandler.UnsubscribeFromCategoryPrefix}{payload.NewsItemSignalCategoryId.Value}"
                            ));
                        }
                        else
                        {
                            inlineButtons.Add(InlineKeyboardButton.WithCallbackData(
                                $"➕ Subscribe to {TelegramMessageFormatter.EscapeMarkdownV2(payload.NewsItemSignalCategoryName.Truncate(20))}",
                                $"{NewsNotificationCallbackHandler.SubscribeToCategoryPrefix}{payload.NewsItemSignalCategoryId.Value}"
                            ));
                        }
                    }

                    if (payload.Buttons != null)
                    {
                        foreach (var btnInfo in payload.Buttons.Where(b => !(b.IsUrl && b.Text.Contains("Read More")))) //  دکمه Read More را دوباره اضافه نکنید
                        {
                            inlineButtons.Add(btnInfo.IsUrl
                                ? InlineKeyboardButton.WithUrl(TelegramMessageFormatter.EscapeMarkdownV2(btnInfo.Text), btnInfo.CallbackDataOrUrl)
                                : InlineKeyboardButton.WithCallbackData(TelegramMessageFormatter.EscapeMarkdownV2(btnInfo.Text), btnInfo.CallbackDataOrUrl));
                        }
                    }

                    InlineKeyboardMarkup? finalKeyboard = inlineButtons.Any() ? new InlineKeyboardMarkup(inlineButtons) : null;

                    _logger.LogDebug("Attempting to send formatted news notification to Telegram UserID {TelegramUserId}.", payload.TargetTelegramUserId);



                    if (!string.IsNullOrWhiteSpace(payload.ImageUrl))
                    {
                        await _telegramApiRetryPolicy.ExecuteAsync(async (ctx, ct) =>
                        {
                            await _telegramMessageSender.SendPhotoAsync(
                                payload.TargetTelegramUserId,
                                payload.ImageUrl,
                                caption: messageTextToSend,
                                parseMode: parseMode,
                                replyMarkup: finalKeyboard,
                                cancellationToken: ct
                            );
                        }, pollyContext, jobCancellationToken);
                    }
                    else
                    {
                        await _telegramApiRetryPolicy.ExecuteAsync(async (ctx, ct) =>
                        {
                            //  تنظیم LinkPreviewOptions برای غیرفعال کردن پیش‌نمایش لینک
                            var defaultLinkPreviewOptions = new LinkPreviewOptions { IsDisabled = true }; //  مثال

                            await _telegramMessageSender.SendTextMessageAsync(
                                payload.TargetTelegramUserId,
                                messageTextToSend,
                                parseMode: parseMode,
                                finalKeyboard,
                                ct, // CancellationToken از Polly context
                                // disableWebPagePreview: true // 📛 حذف شد
                                linkPreviewOptions: defaultLinkPreviewOptions // ✅ استفاده از پارامتر جدید
                            );
                        }, pollyContext, jobCancellationToken);
                    }

                    _logger.LogInformation("Notification successfully sent to Telegram UserID {TelegramUserId}.", payload.TargetTelegramUserId);
                }
                catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || (apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(apiEx, "Non-retryable Telegram API error for UserID {TelegramUserId} (e.g., bot blocked or chat not found). ErrorCode: {ErrorCode}. Message: {ApiMessage}. Job will be marked as failed.",
                        payload.TargetTelegramUserId, apiEx.ErrorCode, apiEx.Message);
                    //  در اینجا می‌توانید کاربر را در دیتابیس به عنوان "غیرقابل دسترس" علامت‌گذاری کنید
                    //  یا نوتیفیکیشن‌های او را برای این نوع پیام غیرفعال نمایید.
                    //  مثال: await _userService.MarkUserAsUnreachableAsync(payload.TargetTelegramUserId.ToString(), "BotBlockedOrChatNotFound", jobCancellationToken);
                    throw; //  اجازه دهید Hangfire جاب را به عنوان ناموفق ثبت کند.
                }
                catch (OperationCanceledException) when (jobCancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Notification sending job for UserID {TelegramUserId} was cancelled by Hangfire scheduler.", payload.TargetTelegramUserId);
                    //  اگر جاب توسط Hangfire کنسل شده، معمولاً نباید خطا throw شود تا Hangfire آن را به عنوان ناموفق در نظر نگیرد.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send notification to UserID {TelegramUserId} after all retries. Job will be marked as failed. Payload Hash: {JobPayloadHash}",
                        payload.TargetTelegramUserId, payload.GetHashCode());
                    throw; //  خطا را دوباره throw کنید تا Hangfire آن را به عنوان ناموفق ثبت کند.
                }
            }
        }
        #endregion

        #region Private Helper Methods
        /// <summary>
        /// Determines if a Telegram API exception should be retried based on its ErrorCode or content.
        /// </summary>
        private static bool ShouldRetryTelegramApiException(ApiRequestException ex)
        {
            // Retry on rate limits (429) or if RetryAfter parameter is present in the response
            if (ex.ErrorCode == 429 || ex.Parameters?.RetryAfter.HasValue == true)
            {
                return true;
            }
            // Retry on common transient server errors (e.g., 500 Internal Server Error, 502 Bad Gateway, etc.)
            if (ex.ErrorCode >= 500 && ex.ErrorCode < 600)
            {
                return true;
            }
            //  می‌توانید خطاهای شبکه یا پیام‌های خطای خاص دیگری را هم اینجا برای retry اضافه کنید.
            //  مثلاً: if (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        #endregion
    }
}