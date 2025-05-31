using System.Threading.Channels;
using Telegram.Bot.Types; // برای Update
using Microsoft.Extensions.Logging; // ✅ اضافه شده برای لاگ‌گذاری Polly
using Polly; // ✅ اضافه شده برای Polly
using Polly.Retry; // ✅ اضافه شده برای سیاست‌های Retry
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramPanel.Queue
{
    /// <summary>
    /// یک صف مبتنی بر Channel برای نگهداری آپدیت‌های دریافتی از تلگرام.
    /// این صف به صورت Singleton رجیستر می‌شود.
    /// </summary>
    public interface ITelegramUpdateChannel
    {
        /// <summary>
        /// یک آپدیت را به صورت ناهمزمان در صف می‌نویسد.
        /// </summary>
        ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک آپدیت را به صورت ناهمزمان از صف می‌خواند.
        /// </summary>
        ValueTask<Update> ReadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام آپدیت‌های موجود در صف را به صورت یک جریان ناهمزمان می‌خواند.
        /// </summary>
        IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// پیاده‌سازی <see cref="ITelegramUpdateChannel"/> با استفاده از <see cref="System.Threading.Channels.Channel{T}"/>.
    /// این کلاس عملیات خواندن و نوشتن را با سیاست‌های تلاش مجدد Polly برای افزایش پایداری پوشش می‌دهد.
    /// </summary>
    public class TelegramUpdateChannel : ITelegramUpdateChannel
    {
        private readonly Channel<Update> _channel;
        private readonly ILogger<TelegramUpdateChannel> _logger;
        private readonly AsyncRetryPolicy _writeRetryPolicy;
        private readonly AsyncRetryPolicy<Update> _readRetryPolicy;

        /// <summary>
        /// سازنده <see cref="TelegramUpdateChannel"/>.
        /// </summary>
        /// <param name="logger">لاگر برای ثبت وقایع و خطاهای Polly.</param>
        /// <param name="capacity">ظرفیت محدوده‌ی صف. (پیش‌فرض: 100)</param>
        public TelegramUpdateChannel(ILogger<TelegramUpdateChannel> logger, int capacity = 100)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait, // اگر صف پر است، تولیدکننده منتظر می‌ماند.
                SingleReader = false, // اجازه می‌دهد چندین مصرف‌کننده (Consumer) از صف بخوانند.
                SingleWriter = false  // اجازه می‌دهد چندین تولیدکننده (Producer) در صف بنویسند.
            };
            _channel = Channel.CreateBounded<Update>(options);

            // تعریف سیاست تلاش مجدد برای عملیات نوشتن (غیر جنریک، چون WriteAsync چیزی برنمی‌گرداند)
            _writeRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException)) // خطاهای گذرا را مدیریت می‌کند، لغو عملیات را نادیده می‌گیرد.
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "TelegramUpdateChannel: WriteAsync failed. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });

            // تعریف سیاست تلاش مجدد برای عملیات خواندن (جنریک، چون ReadAsync یک Update برمی‌گرداند)
            _readRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .OrResult<Update>(result => false) // ✅ بسیار مهم: این متد PolicyBuilder را به PolicyBuilder<Update> تبدیل می‌کند.
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (delegateResult, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(delegateResult.Exception,
                            "TelegramUpdateChannel: ReadAsync failed. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, delegateResult.Exception?.Message);
                    });
        }

        /// <summary>
        /// یک آپدیت را به صورت ناهمزمان در صف می‌نویسد.
        /// عملیات نوشتن با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="update">آبدیت تلگرام برای نوشتن.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات.</param>
        public async ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default)
        {
            await _writeRetryPolicy.ExecuteAsync(async () =>
            {
                await _channel.Writer.WriteAsync(update, cancellationToken);
            });
        }

        /// <summary>
        /// یک آپدیت را به صورت ناهمزمان از صف می‌خواند.
        /// عملیات خواندن با سیاست تلاش مجدد Polly محافظت می‌شود.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات.</param>
        /// <returns>آپدیت خوانده شده از صف.</returns>
        public async ValueTask<Update> ReadAsync(CancellationToken cancellationToken = default)
        {
            return await _readRetryPolicy.ExecuteAsync(async () =>
            {
                return await _channel.Reader.ReadAsync(cancellationToken);
            });
        }

        /// <summary>
        /// تمام آپدیت‌های موجود در صف را به صورت یک جریان ناهمزمان می‌خواند.
        /// (Polly به طور مستقیم روی این متد اعمال نمی‌شود، زیرا IAsyncEnumerable یک جریان است
        /// و مدیریت خطا معمولاً باید توسط مصرف‌کننده این جریان یا از طریق سیاست‌های Polly بر روی
        /// عملیات‌های منفرد درون جریان (مانند ReadAsync) انجام شود.)
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات.</param>
        /// <returns>جریانی ناهمزمان از آپدیت‌ها.</returns>
        public IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}