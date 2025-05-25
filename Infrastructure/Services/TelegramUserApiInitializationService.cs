using Application.Common.Interfaces; // فرض بر این است که ITelegramUserApiClient اینجا تعریف شده
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    public class TelegramUserApiInitializationService : BackgroundService
    {
        private readonly ILogger<TelegramUserApiInitializationService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;

        // --- Configuration for Retry Policy ---
        // این مقادیر می‌توانند از طریق IConfiguration نیز تزریق شوند، اما طبق درخواست، تغییرات فقط در متد اعمال شده‌اند.
        // اگر نیاز به تنظیم از خارج کلاس باشد، این مقادیر باید به پارامترهای سازنده یا یک کلاس Configuration منتقل شوند.

        /// <summary>
        /// حداکثر تعداد تلاش‌های مجدد برای اتصال.
        /// </summary>
        private const int MaxConnectionRetries = 3; // به عنوان مثال، 3 بار تلاش مجدد (مجموعا 4 تلاش)

        /// <summary>
        /// تأخیر اولیه (به میلی‌ثانیه) قبل از اولین تلاش مجدد.
        /// </summary>
        private const int InitialRetryDelayMilliseconds = 3000; // 3 ثانیه

        /// <summary>
        /// ضریب افزایش تأخیر برای هر تلاش مجدد (Exponential Backoff).
        /// مثال: 2.0 باعث می‌شود تأخیرها به صورت 3ثانیه، 6ثانیه، 12ثانیه و ... افزایش یابند.
        /// </summary>
        private const double RetryBackoffFactor = 2.0;

        public TelegramUserApiInitializationService(
            ILogger<TelegramUserApiInitializationService> logger,
            ITelegramUserApiClient userApiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            #region Variables for Retry Logic
            // شمارنده برای تعداد تلاش‌های انجام شده
            int attemptCount = 0;
            // میزان تأخیر فعلی بین تلاش‌ها
            int currentDelayMilliseconds = InitialRetryDelayMilliseconds;
            #endregion

            _logger.LogInformation("Telegram User API Initialization Service is starting.");

            // حلقه تلاش مجدد برای برقراری اتصال
            // این حلقه تا زمانی که اتصال موفقیت‌آمیز باشد یا توکن توقف درخواست شود یا تعداد تلاش‌ها به حداکثر برسد، ادامه می‌یابد.
            while (!stoppingToken.IsCancellationRequested)
            {
                attemptCount++;
                try
                {
                    _logger.LogInformation("Attempting to connect and login to Telegram User API (Attempt {AttemptNumber}/{MaxAttempts})...", attemptCount, MaxConnectionRetries + 1);

                    // --- عملیات اصلی ---
                    // فراخوانی متد اتصال و ورود کاربر. توکن توقف به آن پاس داده می‌شود تا در صورت نیاز، عملیات لغو شود.
                    // این عملیات می‌تواند زمان‌بر باشد.
                    await _userApiClient.ConnectAndLoginAsync(stoppingToken);

                    _logger.LogInformation("Telegram User API client initialized successfully.");
                    // در صورت موفقیت، از حلقه و متد خارج می‌شویم.
                    return;
                }
                catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
                {
                    // این استثنا زمانی رخ می‌دهد که توکن 'stoppingToken' درخواست توقف ارسال کرده باشد.
                    // این یک خروج طبیعی در زمان خاموش شدن برنامه است.
                    _logger.LogWarning(ex, "Telegram User API client initialization was canceled by the application stopping token.");
                    // نیاز به rethrow نیست، سرویس باید متوقف شود.
                    return;
                }
                catch (Exception ex) // برای سایر استثنائات (مانند خطاهای شبکه، مشکلات احراز هویت و ...)
                {
                    #region Security Consideration for Logging
                    // نکته امنیتی: بسیار مهم است که اطمینان حاصل شود جزئیات استثنا (ex)
                    // به خصوص اگر از نوع سفارشی مربوط به _userApiClient باشد،
                    // اطلاعات حساس (مانند توکن‌های API، کلیدها، اطلاعات نشست) را هنگام لاگ شدن افشا نکند.
                    // این مورد بیشتر به پیاده‌سازی _userApiClient و نوع استثناهای آن مربوط می‌شود.
                    #endregion

                    _logger.LogError(ex, "Failed to initialize Telegram User API client on attempt {AttemptNumber}/{MaxAttempts}.", attemptCount, MaxConnectionRetries + 1);

                    // بررسی اینکه آیا تعداد تلاش‌های مجاز به پایان رسیده است.
                    if (attemptCount > MaxConnectionRetries)
                    {
                        _logger.LogCritical(ex, "Exhausted all {MaxAttemptsPlusOne} connection attempts. Telegram User API client could not be initialized. The application startup might fail as designed for critical dependencies.", MaxConnectionRetries + 1);
                        // پس از اتمام تمام تلاش‌ها، استثنای آخر را مجدداً پرتاب می‌کنیم.
                        // این باعث می‌شود که اگر این سرویس برای شروع برنامه حیاتی باشد، برنامه با خطا مواجه شده و متوقف شود (Fail-Fast).
                        throw;
                    }

                    // اگر توکن توقف درخواست شده باشد، از ادامه تلاش‌ها صرف‌نظر می‌کنیم.
                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Cancellation requested after a failed attempt. Halting further retries for Telegram User API client initialization.");
                        return;
                    }

                    _logger.LogInformation("Retrying Telegram User API client initialization in {DelayMilliseconds}ms...", currentDelayMilliseconds);

                    try
                    {
                        // ایجاد تأخیر قبل از تلاش مجدد.
                        // این تأخیر به سرویس فرصت بازیابی می‌دهد و از فشار بیش از حد به سرور جلوگیری می‌کند.
                        // توکن توقف به Task.Delay نیز پاس داده می‌شود تا در صورت خاموش شدن برنامه، تأخیر نیز لغو شود.
                        await Task.Delay(currentDelayMilliseconds, stoppingToken);
                    }
                    catch (OperationCanceledException delayEx) when (stoppingToken.IsCancellationRequested)
                    {
                        // اگر در حین تأخیر، درخواست توقف ارسال شود.
                        _logger.LogWarning(delayEx, "Retry delay for Telegram User API client initialization was canceled by the application stopping token.");
                        return;
                    }

                    // افزایش تأخیر برای تلاش بعدی (Exponential Backoff)
                    // این کار از الگوی افزایش نمایی تأخیر پیروی می‌کند تا در صورت بروز مشکلات مداوم، فشار کمتری به سرور وارد شود.
                    // محدود کردن حداکثر تأخیر نیز می‌تواند در نظر گرفته شود، اما در اینجا ساده نگه داشته شده است.
                    currentDelayMilliseconds = (int)(currentDelayMilliseconds * RetryBackoffFactor);
                }
            }

            // این بخش از کد تنها در صورتی اجرا می‌شود که حلقه while به دلیل stoppingToken.IsCancellationRequested خاتمه یابد
            // قبل از اینکه اولین تلاش در حلقه انجام شود (که بسیار نادر است).
            if (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Telegram User API client initialization process was externally canceled before any attempt could be made or completed.");
            }
        }
    }
}