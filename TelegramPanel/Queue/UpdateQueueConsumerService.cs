using Microsoft.Extensions.DependencyInjection; // برای IServiceScopeFactory و CreateScope
using Microsoft.Extensions.Hosting;             // برای BackgroundService
using Microsoft.Extensions.Logging;             // برای ILogger
using Polly;                                    // اضافه شده برای Polly.Context و Extension Methods
using Polly.Retry;                              // اضافه شده برای AsyncRetryPolicy
using TelegramPanel.Application.Interfaces;     // برای ITelegramUpdateProcessor
using TelegramPanel.Infrastructure;             // برای ITelegramMessageSender (فرض شده در Infrastructure است)

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
        private readonly AsyncRetryPolicy _processingRetryPolicy; // سیاست Polly برای پردازش آپدیت

        #region Private Fields
        // این فیلدها در کد اصلی ارائه شده برای ExecuteAsync استفاده نمی‌شوند، اما برای حفظ ساختار حفظ شده‌اند.
        private WTelegram.Client? _client; // فرض شده این فیلد وجود دارد اما در اینجا استفاده نمی‌شود.
        private SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1); // فرض شده این فیلد وجود دارد اما در اینجا استفاده نمی‌شود.
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

            // تعریف سیاست تلاش مجدد برای پردازش آپدیت‌ها.
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
                // ✅ مهم: این 'await foreach' خودش یک مکانیسم غیربلاک‌کننده و کارآمد برای مصرف Channel است.
                // نیازی به Task.Run اضافی در اینجا نیست، زیرا هر آپدیت قبلاً توسط متد Dispatcher (مثلاً
                // StartUpdateChannelPipeline در TelegramUserApiClient) به یک Task.Run منتقل شده است.
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

                    // ایجاد یک دیکشنری برای اطلاعات زمینه‌ای Polly.Context
                    // این اطلاعات برای لاگ‌گذاری بهتر در Polly OnRetry استفاده می‌شود.
                    var pollyContextData = new Dictionary<string, object>
                    {
                        { "UpdateId", updateId },
                        { "UpdateType", updateType.ToString() }
                    };

                    // ایجاد Polly.Context با استفاده از سازنده‌ای که Dictionary<string, object> را می‌پذیرد.
                    var pollyContext = new Polly.Context($"UpdateProcessing_{updateId}", pollyContextData);

                    // استفاده از BeginScope برای اضافه کردن اطلاعات زمینه‌ای به تمام لاگ‌های تولید شده در این تکرار حلقه.
                    using (_logger.BeginScope(pollyContextData)) // استفاده از همان Dictionary برای Log Scope
                    {
                        try
                        {
                            _logger.LogDebug("Dequeued update for processing.");

                            // اعمال سیاست تلاش مجدد Polly به منطق پردازش اصلی آپدیت.
                            // ✅ این Task/Thread که در حال حاضر در آن هستیم، از قبل توسط Task.Run در لایه قبلی
                            // (مثلاً StartUpdateChannelPipeline) ایجاد شده است.
                            // افزودن Task.Run در اینجا باعث "Thread hopping" غیرضروری و کاهش کارایی می‌شود.
                            await _processingRetryPolicy.ExecuteAsync(async (context, ct) =>
                            {
                                // ایجاد یک Scope جدید از Dependency Injection برای هر پردازش آپدیت،
                                // این کار تضمین می‌کند که وابستگی‌های Scoped (مانند DbContext) به درستی ایزوله و مدیریت شوند.
                                await using (var scope = _scopeFactory.CreateAsyncScope())
                                {
                                    var updateProcessor = scope.ServiceProvider.GetRequiredService<ITelegramUpdateProcessor>();
                                    _logger.LogInformation("Passing update to ITelegramUpdateProcessor.");
                                    // passing the original stoppingToken to ProcessUpdateAsync, which Polly will also observe
                                    // ProcessUpdateAsync باید خودش به درستی async/await (برای I/O) یا Task.Run (برای CPU-bound) را رعایت کند.
                                    await updateProcessor.ProcessUpdateAsync(update, stoppingToken);
                                    _logger.LogInformation("Update processed successfully by ITelegramUpdateProcessor.");
                                }
                            }, pollyContext, stoppingToken); // ارسال Context و CancellationToken به Polly

                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("Processing of update was canceled due to stopping token.");
                            // اینجا از 'break' استفاده می‌کنیم تا از حلقه 'await foreach' خارج شویم
                            // و به طور طبیعی به بخش 'finally' برویم و سرویس را متوقف کنیم.
                            break;
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
                                                cancellationToken: CancellationToken.None); // استفاده از CancellationToken.None برای اطمینان از ارسال پیام خطا حتی در زمان توقف سرویس.
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
                // این خطا زمانی رخ می‌دهد که stoppingToken در زمان await _updateChannel.ReadAllAsync() فعال شود.
                _logger.LogInformation("Update Queue Consumer Service was canceled gracefully during channel read operation.");
            }
            catch (Exception ex)
            {
                // هر خطای غیرمنتظره‌ای که در حین خواندن از Channel رخ دهد.
                _logger.LogError(ex, "Update Queue Consumer Service encountered an unexpected error while reading from the channel. Service will terminate.");
                throw; // Re-throw to ensure the host marks the service as failed
            }
            finally
            {
                _logger.LogInformation("Update Queue Consumer Service has stopped.");
                // ایمن‌سازی در برابر ObjectDisposedException در صورت تلاش برای Disposed کردن مجدد
                try
                {
                    _connectionLock.Dispose(); // این فیلد در این کلاس استفاده نشده، اما برای کامل بودن.
                }
                catch (ObjectDisposedException ode)
                {
                    _logger.LogWarning(ode, "Connection lock was already disposed during service shutdown.");
                }
            }
        }

        /// <summary>
        /// هنگام توقف سرویس Hosted فراخوانی می‌شود.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات توقف.</param>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Update Queue Consumer Service stop requested.");
            // فراخوانی متد پایه برای مدیریت توقف BackgroundService
            await base.StopAsync(cancellationToken);
            _logger.LogInformation("Update Queue Consumer Service has finished stopping procedures.");
        }
    }
}