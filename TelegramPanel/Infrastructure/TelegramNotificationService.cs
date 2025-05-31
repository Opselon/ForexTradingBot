// File: TelegramPanel/Infrastructure/TelegramNotificationService.cs
#region Usings
using Application.Common.Interfaces; // برای INotificationService (از پروژه Application)
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types.Enums;   // برای ParseMode
using Polly;                      // ✅ اضافه شده برای Polly
using Polly.Retry;                // ✅ اضافه شده برای سیاست‌های Retry
using System;
using System.Collections.Generic; // برای Dictionary در Context
using System.Threading;
using System.Threading.Tasks;
// ITelegramMessageSender باید در همین namespace یا یک using صحیح داشته باشد.
// فرض می‌کنیم ITelegramMessageSender در TelegramPanel.Infrastructure تعریف شده.
#endregion

namespace TelegramPanel.Infrastructure
{
    /// <summary>
    /// سرویسی برای ارسال اعلان‌ها از طریق تلگرام.
    /// این سرویس اینترفیس <see cref="INotificationService"/> را پیاده‌سازی می‌کند
    /// و از <see cref="ITelegramMessageSender"/> برای ارسال پیام‌های واقعی استفاده می‌کند.
    /// از Polly برای افزایش پایداری در برابر خطاهای گذرا در حین ارسال پیام استفاده می‌شود.
    /// </summary>
    public class TelegramNotificationService : INotificationService
    {
        #region Private Readonly Fields
        private readonly ITelegramMessageSender _telegramMessageSender;
        private readonly ILogger<TelegramNotificationService> _logger;
        private readonly AsyncRetryPolicy _notificationRetryPolicy; // ✅ جدید: سیاست Polly برای ارسال اعلان
        #endregion

        #region Constructor
        public TelegramNotificationService(
            ITelegramMessageSender telegramMessageSender,
            ILogger<TelegramNotificationService> logger)
        {
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ✅ تعریف سیاست تلاش مجدد برای ارسال اعلان‌ها.
            // این سیاست هر Exception را مدیریت می‌کند به جز OperationCanceledException و TaskCanceledException
            // که نشان‌دهنده لغو عمدی عملیات هستند.
            _notificationRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var chatId = context.TryGetValue("ChatId", out var id) ? (long?)id : null;
                        var messagePreview = context.TryGetValue("MessagePreview", out var msg) ? msg?.ToString() : "N/A";
                        _logger.LogWarning(exception,
                            "PollyRetry: Failed to send Telegram notification to ChatID {ChatId}. Retrying in {TimeSpan} for attempt {RetryAttempt}. Message preview: '{MessagePreview}'. Error: {Message}",
                            chatId, timeSpan, retryAttempt, messagePreview, exception.Message);
                    });
        }
        #endregion

        #region INotificationService Implementation
        /// <summary>
        /// اعلان را به شناسه گیرنده مشخص شده ارسال می‌کند.
        /// این متد از سیاست تلاش مجدد Polly برای افزایش پایداری در ارسال پیام استفاده می‌کند.
        /// </summary>
        /// <param name="recipientIdentifier">شناسه تلگرام کاربر (ChatID) به عنوان رشته.</param>
        /// <param name="message">متن پیام برای ارسال.</param>
        /// <param name="useRichText">اگر true باشد، پیام با فرمت MarkdownV2 ارسال می‌شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        public async Task SendNotificationAsync(string recipientIdentifier, string message, bool useRichText = false, CancellationToken cancellationToken = default)
        {
            // recipientIdentifier در اینجا همان Telegram User ID (به صورت رشته) است.
            if (!long.TryParse(recipientIdentifier, out long chatId))
            {
                _logger.LogError("Invalid recipient identifier for Telegram notification: {RecipientIdentifier}. Expected a long ChatID.", recipientIdentifier);
                return; // یا throw exception
            }

            ParseMode? parseMode = useRichText ? ParseMode.MarkdownV2 : null;
            string messageToSend = message;

            // اگر از MarkdownV2 استفاده می‌کنیم، و فرمتتر ما مسئول escape کردن است، اینجا باید آن را فراخوانی کنیم.
            // اما اگر پیام از لایه Application از قبل با فرمت MarkdownV2 آماده شده، نیازی به escape مجدد نیست.
            // فرض فعلی: پیام ورودی 'message' اگر useRichText=true باشد، از قبل برای MarkdownV2 آماده است.
            // اگر نه، TelegramMessageFormatter باید توابع Escape هم داشته باشد.
            // مثال:
            // if (useRichText && parseMode == ParseMode.MarkdownV2)
            // {
            //     messageToSend = TelegramMessageFormatter.EscapeMarkdownV2(message); // اگر TelegramMessageFormatter این متد را دارد
            // }


            _logger.LogInformation("Sending Telegram notification to ChatID {ChatId}. RichText: {UseRichText}, Message (partial): {MessagePartial}",
                chatId, useRichText, message.Length > 50 ? message.Substring(0, 50) + "..." : message);

            // ✅ آماده‌سازی Context برای Polly با اطلاعات مرتبط با پیام برای لاگ‌گذاری بهتر.
            var pollyContext = new Polly.Context($"NotificationToChat_{chatId}", new Dictionary<string, object>
            {
                { "ChatId", chatId },
                { "MessagePreview", message.Length > 100 ? message.Substring(0, 100) + "..." : message }
            });

            try
            {
                // ✅ اعمال سیاست تلاش مجدد Polly بر روی SendTextMessageAsync
                await _notificationRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _telegramMessageSender.SendTextMessageAsync(chatId, messageToSend, parseMode, null, ct); // از ct برای Polly استفاده می‌شود
                }, pollyContext, cancellationToken); // ارسال Context و CancellationToken به Polly
            }
            catch (Exception ex)
            {
                // این catch block تنها در صورتی فعال می‌شود که تمام تلاش‌های Polly با شکست مواجه شوند.
                _logger.LogError(ex, "Failed to send Telegram notification to ChatID {ChatId} after all retries.", chatId);
                // می‌توانید خطا را دوباره throw کنید اگر لایه بالاتر باید از آن مطلع شود.
                // throw;
            }
        }
        #endregion
    }
}