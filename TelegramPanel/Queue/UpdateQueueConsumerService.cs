// File: TelegramPanel.Queue/UpdateQueueConsumerService.cs

using Microsoft.Extensions.DependencyInjection; // برای IServiceScopeFactory و CreateScope
using Microsoft.Extensions.Hosting;             // برای BackgroundService
using Microsoft.Extensions.Logging;             // برای ILogger
using TelegramPanel.Application.Interfaces;     // برای ITelegramUpdateProcessor
using TelegramPanel.Infrastructure;             // برای ITelegramMessageSender (فرض شده در Infrastructure است)
using Polly;                                    // ✅ اضافه شده برای Polly.Context و Extension Methods
using Polly.Retry;                              // ✅ اضافه شده برای AsyncRetryPolicy
using System;
using System.Collections.Generic;               // برای Dictionary
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;                       // برای Update

namespace TelegramPanel.Queue
{
    /// <summary>
    /// یک سرویس پس‌زمینه (BackgroundService) که به طور مداوم آپدیت‌های تلگرام را
    /// از یک صف مشترک (<see cref="ITelegramUpdateChannel"/>) می‌خواند
    /// و آن‌ها را برای پردازش به <see cref="ITelegramUpdateProcessor"/> ارسال می‌کند.
    /// این سرویس اطمینان حاصل می‌کند که هر آپدیت در یک Scope جداگانه از Dependency Injection پردازش می‌شود
    /// و از Polly برای افزایش پایداری در برابر خطاهای گذرا استفاده می‌کند.
    /// </summary>
    public class UpdateQueueConsumerService : BackgroundService
    {
        private readonly ILogger<UpdateQueueConsumerService> _logger;
        private readonly ITelegramUpdateChannel _updateChannel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AsyncRetryPolicy _processingRetryPolicy; // ✅ جدید: سیاست Polly برای پردازش آپدیت

        #region Private Fields
        // این فیلدها در کد اصلی ارائه شده برای ExecuteAsync استفاده نمی‌شوند، اما برای حفظ ساختار حفظ شده‌اند.
        private WTelegram.Client? _client;
        private SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        #endregion

        /// <summary>
        /// سازنده سرویس مصرف‌کننده صف آپدیت.
        /// </summary>
        /// <param name="logger">سرویس لاگینگ.</param>
        /// <param name="updateChannel">کانال صف برای خواندن آپدیت‌ها.</param>
        /// <param name="scopeFactory">فکتوری برای ایجاد Scope های DI جدید برای هر پردازش آپدیت.</param>
        public UpdateQueueConsumerService(
            ILogger<UpdateQueueConsumerService> logger,
            ITelegramUpdateChannel updateChannel,
            IServiceScopeFactory scopeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

            // ✅ تعریف سیاست تلاش مجدد برای پردازش آپدیت‌ها.
            // این سیاست هر Exception را مدیریت می‌کند به جز OperationCanceledException و TaskCanceledException
            // که نشان‌دهنده لغو عمدی عملیات هستند.
            _processingRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        // استخراج اطلاعات آپدیت از Context Polly برای لاگ‌گذاری بهتر.
                        // (TryGetValue یک Extension Method است که در صورت وجود Polly نصب شده، کار می‌کند.)
                        var updateId = context.TryGetValue("UpdateId", out var id) ? (int?)id : null;
                        var updateType = context.TryGetValue("UpdateType", out var type) ? type?.ToString() : "N/A";
                        _logger.LogWarning(exception,
                            "PollyRetry: Processing update {UpdateId} (Type: {UpdateType}) failed with a transient error. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            updateId, updateType, timeSpan, retryAttempt, exception.Message);
                    });
        }

        /// <summary>
        /// متد اصلی اجرای سرویس پس‌زمینه.
        /// این متد در یک حلقه بی‌نهایت (تا زمانی که توکن توقف فعال نشده) آپدیت‌ها را از صف می‌خواند.
        /// </summary>
        /// <param name="stoppingToken">توکنی که نشان‌دهنده درخواست توقف سرویس است.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Update Queue Consumer Service is starting and waiting for updates...");

            try
            {
                // حلقه اصلی برای خواندن از کانال صف.
                // .WithCancellation(stoppingToken) تضمین می‌کند که حلقه در صورت درخواست لغو متوقف می‌شود.
                await foreach (var update in _updateChannel.ReadAllAsync(stoppingToken).WithCancellation(stoppingToken))
                {
                    // بررسی مجدد stoppingToken در داخل حلقه برای پاسخ سریع‌تر به توقف
                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Update Queue Consumer Service received stop signal while processing queue.");
                        break;
                    }

                    var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
                    var updateId = update.Id;
                    var updateType = update.Type;

                    // ایجاد یک دیکشنری برای اطلاعات زمینه‌ای لاگ (Log Scope)
                    var logScopeProps = new Dictionary<string, object?>
                    {
                        ["UpdateId"] = updateId,
                        ["UpdateType"] = updateType,
                        ["TelegramUserId"] = userId
                    };

                    // ✅ اصلاح: ساخت Polly.Context با استفاده از سازنده‌ای که Dictionary<string, object> را می‌پذیرد.
                    // این کار نیاز به Extension Method 'WithData' را برطرف می‌کند.
                    var pollyContext = new Polly.Context($"UpdateProcessing_{updateId}", new Dictionary<string, object>
                    {
                        { "UpdateId", updateId },
                        { "UpdateType", updateType.ToString() }
                    });

                    // استفاده از BeginScope برای اضافه کردن اطلاعات زمینه‌ای به تمام لاگ‌های تولید شده در این تکرار حلقه.
                    using (_logger.BeginScope(logScopeProps))
                    {
                        try
                        {
                            _logger.LogDebug("Dequeued update for processing.");

                            // اعمال سیاست تلاش مجدد Polly به منطق پردازش اصلی آپدیت.
                            await _processingRetryPolicy.ExecuteAsync(async (context, ct) =>
                            {
                                // ایجاد یک Scope جدید از Dependency Injection برای هر پردازش آپدیت،
                                // این کار تضمین می‌کند که وابستگی‌های Scoped (مانند DbContext) به درستی ایزوله و مدیریت شوند.
                                await using (var scope = _scopeFactory.CreateAsyncScope())
                                {
                                    var updateProcessor = scope.ServiceProvider.GetRequiredService<ITelegramUpdateProcessor>();
                                    _logger.LogInformation("Passing update to ITelegramUpdateProcessor.");
                                    // passing the original stoppingToken to ProcessUpdateAsync, which Polly will also observe
                                    await updateProcessor.ProcessUpdateAsync(update, stoppingToken);
                                    _logger.LogInformation("Update processed successfully by ITelegramUpdateProcessor.");
                                }
                            }, pollyContext, stoppingToken); // ارسال Context و CancellationToken به Polly

                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("Processing of update was canceled due to stopping token.");
                            break; // خروج از حلقه foreach برای توقف با آرامش
                        }
                        catch (Exception ex)
                        {
                            // ثبت خطای جامع هنگام پردازش یک آپدیت خاص، پس از تمام تلاش‌های مجدد Polly.
                            _logger.LogError(ex, "An unhandled error occurred while processing update {UpdateId} (Type: {UpdateType}) from the queue after retries.", updateId, updateType);

                            // در محیط Production، می‌توانید این آپدیت ناموفق را به یک "Dead Letter Queue" (DLQ)
                            // منتقل کنید تا بعداً بررسی شود و از دست نرود.
                            // ارسال یک پیام خطا به کاربر (اختیاری).
                            if (userId.HasValue)
                            {
                                try
                                {
                                    await using (var errorScope = _scopeFactory.CreateAsyncScope())
                                    {
                                        var messageSender = errorScope.ServiceProvider.GetService<ITelegramMessageSender>();
                                        if (messageSender != null)
                                        {
                                            await messageSender.SendTextMessageAsync(
                                                userId.Value,
                                                "Sorry, an unexpected error occurred while processing your request. Our team has been notified.",
                                                cancellationToken: CancellationToken.None);
                                        }
                                    }
                                }
                                catch (Exception sendEx)
                                {
                                    _logger.LogError(sendEx, "Failed to send error notification message to user {UserId} after processing error.", userId);
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Update Queue Consumer Service was canceled gracefully during channel read operation.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update Queue Consumer Service encountered an unexpected error while reading from the channel.");
                throw; // Re-throw to ensure the host marks the service as failed
            }
            finally
            {
                _logger.LogInformation("Update Queue Consumer Service has stopped.");
            }
        }

        /// <summary>
        /// هنگام توقف سرویس Hosted فراخوانی می‌شود.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات توقف.</param>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Update Queue Consumer Service stop requested.");
            // می‌توان Writer کانال را Complete کرد تا ReadAllAsync به طور طبیعی پایان یابد،
            // اگرچه stoppingToken معمولاً همین کار را انجام می‌دهد.
            // _updateChannel.CompleteWriter(); // این متد در اینترفیس فعلی ITelegramUpdateChannel وجود ندارد.
            await base.StopAsync(cancellationToken);
            _logger.LogInformation("Update Queue Consumer Service has finished stopping procedures.");

            // ایمن‌سازی در برابر ObjectDisposedException در صورت تلاش برای Disposed کردن مجدد
            try
            {
                _connectionLock.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // نادیده گرفتن اگر قبلاً Dispose شده باشد
            }
        }
    }
}