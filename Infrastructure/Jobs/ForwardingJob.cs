// File: Infrastructure\Jobs\ForwardingJob.cs
using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic; // برای List
using System.Linq; // برای Any()
using System.Threading;
using System.Threading.Tasks;
using TL; // این using لازم است تا بتوانید از MessageEntity و Peer و سایر انواع Telegram.Bot استفاده کنید
using Polly; // اضافه کردن using برای Polly
using Polly.Retry; // اضافه کردن using برای سیاست‌های Retry

namespace Infrastructure.Jobs
{
    /// <summary>
    /// یک وظیفه پس‌زمینه (Hangfire Job) برای پردازش و فوروارد کردن پیام‌ها.
    /// این کلاس عملیات فورواردینگ را با استفاده از <see cref="IForwardingService"/> انجام می‌دهد
    /// و از Polly برای افزایش پایداری در برابر خطاهای گذرا استفاده می‌کند.
    /// </summary>
    public class ForwardingJob
    {
        private readonly IForwardingService _forwardingService;
        private readonly ILogger<ForwardingJob> _logger;
        private readonly AsyncRetryPolicy _retryPolicy; // فیلد جدید برای سیاست Polly

        /// <summary>
        /// نمونه جدیدی از کلاس ForwardingJob را ایجاد می‌کند.
        /// </summary>
        /// <param name="forwardingService">سرویس فورواردینگ برای پردازش پیام‌ها.</param>
        /// <param name="logger">لاگر برای ثبت اطلاعات و خطاها.</param>
        public ForwardingJob(IForwardingService forwardingService, ILogger<ForwardingJob> logger)
        {
            _forwardingService = forwardingService ?? throw new ArgumentNullException(nameof(forwardingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // مقداردهی اولیه سیاست Polly برای تلاش مجدد در برابر خطاهای گذرا.
            // این سیاست هر Exception را مدیریت می‌کند به جز OperationCanceledException و TaskCanceledException
            // که نشان‌دهنده لغو عمدی عملیات هستند.
            _retryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        // لاگ‌گذاری در هنگام هر بار تلاش مجدد
                        _logger.LogWarning(exception,
                            "ForwardingJob: Transient error encountered while processing message. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }

        /// <summary>
        /// متدی که توسط Hangfire Scheduler برای پردازش یک پیام فورواردینگ صدا زده می‌شود.
        /// این متد عملیات پردازش پیام را با استفاده از <see cref="IForwardingService"/> انجام می‌دهد
        /// و توسط سیاست تلاش مجدد Polly برای پایداری بیشتر محافظت می‌شود.
        /// </summary>
        /// <param name="sourceChannelIdForMatching">شناسه کانال مبدأ برای تطبیق با قوانین فورواردینگ.</param>
        /// <param name="originalMessageId">شناسه منحصر به فرد پیام اصلی در کانال مبدأ.</param>
        /// <param name="rawSourcePeerIdForApi">شناسه خام peer مبدأ برای استفاده در فراخوانی‌های API (مثلاً Telegram API).</param>
        /// <param name="messageContent">محتوای متنی پیام.</param>
        /// <param name="messageEntities">آرایه‌ای از MessageEntityها (مثلاً برای فرمت‌بندی متن) در پیام.</param>
        /// <param name="senderPeerForFilter">اطلاعات peer فرستنده پیام، برای استفاده در فیلترینگ.</param>
        /// <param name="mediaGroupItems">لیستی از آیتم‌های گروه رسانه‌ای (عکس، ویدیو و غیره) مرتبط با پیام.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات.</param>
        public async Task ProcessMessageAsync(
           long sourceChannelIdForMatching,
           long originalMessageId,
           long rawSourcePeerIdForApi,
           string messageContent,
           MessageEntity[]? messageEntities,
           Peer? senderPeerForFilter,
           List<InputMediaWithCaption>? mediaGroupItems,
           CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "HANGFIRE_JOB: Starting job for MsgID: {OriginalMsgId}, SourceForMatching: {SourceForMatching}, RawSourceForApi: {RawSourceForApi}. Content Preview: '{ContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                originalMessageId, sourceChannelIdForMatching, rawSourcePeerIdForApi, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

            try
            {
                // اعمال سیاست تلاش مجدد Polly به عملیات اصلی سرویس فورواردینگ.
                // اگر _forwardingService.ProcessMessageAsync دچار خطای گذرا شود، Polly طبق سیاست تعریف شده تلاش مجدد می‌کند.
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    await _forwardingService.ProcessMessageAsync(
                        sourceChannelIdForMatching,
                        originalMessageId,
                        rawSourcePeerIdForApi,
                        messageContent,
                        messageEntities,
                        senderPeerForFilter,
                        mediaGroupItems,
                        cancellationToken);
                });

                _logger.LogInformation(
                    "HANGFIRE_JOB: Successfully completed job for MsgID: {OriginalMsgId}, SourceForMatching: {SourceForMatching}",
                    originalMessageId, sourceChannelIdForMatching);
            }
            catch (Exception ex)
            {
                // اگر Polly تمام تلاش‌های مجدد را انجام دهد و عملیات همچنان با شکست مواجه شود،
                // یا اگر خطایی رخ دهد که توسط سیاست Polly مدیریت نمی‌شود (مثلاً OperationCanceledException),
                // این بلاک catch خطا را ثبت می‌کند.
                _logger.LogError(ex,
                    "HANGFIRE_JOB: Failed to process message after all retries for MsgID: {OriginalMsgId}, SourceForMatching: {SourceForMatching}. Error: {ErrorMessage}",
                    originalMessageId, sourceChannelIdForMatching, ex.Message);
                // معمولاً در اینجا خطای نهایی دوباره پرتاب می‌شود تا Hangfire بتواند آن را به عنوان یک وظیفه ناموفق علامت‌گذاری کند.
                throw;
            }
        }

        /// <summary>
        /// تابع کمکی برای کوتاه کردن رشته‌ها برای نمایش در لاگ‌ها.
        /// </summary>
        /// <param name="str">رشته ورودی.</param>
        /// <param name="maxLength">حداکثر طول مجاز برای رشته.</param>
        /// <returns>رشته کوتاه شده یا "[null_or_empty]" اگر رشته ورودی null یا خالی باشد.</returns>
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }
    }
}