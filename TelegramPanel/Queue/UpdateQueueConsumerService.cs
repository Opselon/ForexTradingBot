using Microsoft.Extensions.DependencyInjection; // برای IServiceScopeFactory یا IServiceProvider و CreateScope
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types; // برای Update
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure; // برای ITelegramUpdateProcessor
// TelegramPanel.Queue باید namespace فعلی باشد، پس نیازی به using آن نیست

namespace TelegramPanel.Queue
{
    /// <summary>
    /// یک سرویس پس‌زمینه (BackgroundService) که به طور مداوم آپدیت‌های تلگرام را
    /// از یک صف مشترک (<see cref="ITelegramUpdateChannel"/>) می‌خواند
    /// و آن‌ها را برای پردازش به <see cref="ITelegramUpdateProcessor"/> ارسال می‌کند.
    /// این سرویس اطمینان حاصل می‌کند که هر آپدیت در یک Scope جداگانه از Dependency Injection پردازش می‌شود.
    /// </summary>
    public class UpdateQueueConsumerService : BackgroundService
    {
        private readonly ILogger<UpdateQueueConsumerService> _logger;
        private readonly ITelegramUpdateChannel _updateChannel;
        private readonly IServiceScopeFactory _scopeFactory; // ✅ استفاده از IServiceScopeFactory به جای IServiceProvider مستقیم

        /// <summary>
        /// سازنده سرویس مصرف‌کننده صف آپدیت.
        /// </summary>
        /// <param name="logger">سرویس لاگینگ.</param>
        /// <param name="updateChannel">کانال صف برای خواندن آپدیت‌ها.</param>
        /// <param name="scopeFactory">فکتوری برای ایجاد Scope های DI جدید برای هر پردازش آپدیت.</param>
        public UpdateQueueConsumerService(
            ILogger<UpdateQueueConsumerService> logger,
            ITelegramUpdateChannel updateChannel,
            IServiceScopeFactory scopeFactory) // ✅ تزریق IServiceScopeFactory
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        /// <summary>
        /// متد اصلی اجرای سرویس پس‌زمینه.
        /// این متد در یک حلقه بی‌نهایت (تا زمانی که توکن توقف فعال نشده) آپدیت‌ها را از صف می‌خواند.
        /// </summary>
        /// <param name="stoppingToken">توکنی که نشان‌دهنده درخواست توقف سرویس است.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Update Queue Consumer Service is starting and waiting for updates...");

            // حلقه اصلی برای خواندن از کانال صف.
            // ReadAllAsync یک IAsyncEnumerable برمی‌گرداند که تا زمانی که کانال باز است و stoppingToken کنسل نشده، آپدیت‌ها را yield می‌کند.
            await foreach (var update in _updateChannel.ReadAllAsync(stoppingToken).WithCancellation(stoppingToken))
            {
                // بررسی مجدد stoppingToken در داخل حلقه برای پاسخ سریع‌تر به توقف (اگرچه ReadAllAsync هم آن را در نظر می‌گیرد).
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Update Queue Consumer Service received stop signal while processing queue.");
                    break;
                }

                var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
                // ایجاد یک دیکشنری برای اطلاعات زمینه‌ای لاگ (Log Scope)
                var logScopeProps = new Dictionary<string, object?>
                {
                    ["UpdateId"] = update.Id,
                    ["UpdateType"] = update.Type,
                    ["TelegramUserId"] = userId
                };

                // استفاده از BeginScope برای اضافه کردن اطلاعات زمینه‌ای به تمام لاگ‌های تولید شده در این تکرار حلقه.
                using (_logger.BeginScope(logScopeProps))
                {
                    try
                    {
                        _logger.LogDebug("Dequeued update for processing.");

                        // ایجاد یک Scope جدید از Dependency Injection برای هر پردازش آپدیت.
                        // این بسیار مهم است زیرا:
                        //   1. سرویس‌های Scoped (مانند DbContext، IUserService، و اکثر Repository ها)
                        //      برای هر درخواست/آپدیت، یک نمونه جدید دریافت می‌کنند و از تداخل داده بین آپدیت‌ها جلوگیری می‌شود.
                        //   2. منابع Scoped به درستی Dispose می‌شوند پس از اتمام پردازش آپدیت.
                        // استفاده از IServiceScopeFactory برای این منظور بهترین روش است.
                        await using (var scope = _scopeFactory.CreateAsyncScope()) // استفاده از CreateAsyncScope اگر سرویس‌ها IAsyncDisposable هستند
                        {
                            var updateProcessor = scope.ServiceProvider.GetRequiredService<ITelegramUpdateProcessor>();
                            _logger.LogInformation("Passing update to ITelegramUpdateProcessor.");
                            await updateProcessor.ProcessUpdateAsync(update, stoppingToken);
                            _logger.LogInformation("Update processed successfully by ITelegramUpdateProcessor.");
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Processing of update was canceled due to stopping token.");
                        // اگر برنامه در حال خاموش شدن است، ممکن است بخواهیم از پردازش بیشتر جلوگیری کنیم.
                        break;
                    }
                    catch (Exception ex)
                    {
                        // ثبت خطای جامع هنگام پردازش یک آپدیت خاص.
                        // این خطا نباید باعث توقف کل سرویس مصرف‌کننده صف شود (حلقه باید ادامه یابد).
                        _logger.LogError(ex, "An unhandled error occurred while processing an update from the queue.");

                        // در محیط Production، می‌توانید این آپدیت ناموفق را به یک "Dead Letter Queue" (DLQ)
                        // منتقل کنید تا بعداً بررسی شود و از دست نرود.
                        // یا اینکه یک سیستم Retry با backoff پیاده‌سازی کنید (برای خطاهای موقتی).
                        // مثال ساده برای ارسال پیام به کاربر در صورت امکان:
                        if (userId.HasValue)
                        {
                            // تلاش برای ارسال پیام خطا (با احتیاط، چون ممکن است خود ارسال هم خطا دهد)
                            try
                            {
                                // اینجا هم باید یک scope جدید ایجاد شود اگر ITelegramMessageSender, Scoped است
                                await using (var errorScope = _scopeFactory.CreateAsyncScope())
                                {
                                    var messageSender = errorScope.ServiceProvider.GetService<ITelegramMessageSender>();
                                    if (messageSender != null)
                                    {
                                        await messageSender.SendTextMessageAsync(
                                            userId.Value,
                                            "Sorry, an unexpected error occurred while processing your request. Our team has been notified.",
                                            cancellationToken: CancellationToken.None); // استفاده از CancellationToken.None برای اطمینان از ارسال پیام خطا
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
            _logger.LogInformation("Update Queue Consumer Service has gracefully stopped.");
        }

        /// <summary>
        /// هنگام توقف سرویس Hosted فراخوانی می‌شود.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Update Queue Consumer Service stop requested.");
            //  می‌توانید در اینجا Writer کانال را Complete کنید تا ReadAllAsync به طور طبیعی پایان یابد،
            //  اگرچه stoppingToken هم همین کار را انجام می‌دهد.
            //  _updateChannel.CompleteWriter();
            await base.StopAsync(cancellationToken);
            _logger.LogInformation("Update Queue Consumer Service has finished stopping procedures.");
        }
    }
}