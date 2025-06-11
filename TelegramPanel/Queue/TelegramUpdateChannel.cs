using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Threading.Channels;
using Telegram.Bot.Types;

namespace TelegramPanel.Queue
{
    public interface ITelegramUpdateChannel
    {
        ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default);
        ValueTask<Update> ReadAsync(CancellationToken cancellationToken = default);
        IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken = default);
    }

    public class TelegramUpdateChannel : ITelegramUpdateChannel
    {
        private readonly Channel<Update> _channel;
        private readonly ILogger<TelegramUpdateChannel> _logger;
        private readonly AsyncRetryPolicy _writeRetryPolicy;
        private readonly AsyncRetryPolicy<Update> _readRetryPolicy;
        private readonly int _maxRetryAttempts;

        public TelegramUpdateChannel(ILogger<TelegramUpdateChannel> logger, int capacity = 500)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxRetryAttempts = 3;

            var options = new BoundedChannelOptions(capacity)
            {
                // ✅ تغییر مهم: بازگشت به Wait. این تضمین می‌کند که هیچ آپدیتی در صف داخلی از دست نمی‌رود.
                // مدیریت بلاک شدن این WriteAsync (اگر Channel پر باشد) به لایه فراخواننده (TelegramBotService) واگذار می‌شود.
                FullMode = BoundedChannelFullMode.Wait, // ✅ بازگشت به Wait
                SingleReader = false,
                SingleWriter = false
            };
            _channel = Channel.CreateBounded<Update>(options);

            _writeRetryPolicy = Policy
                .Handle<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: _maxRetryAttempts,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "TelegramUpdateChannel: WriteAsync failed. Retrying in {TimeSpan} (attempt {RetryAttempt}/{MaxRetries}). Error: {Message}",
                            timeSpan, retryAttempt, _maxRetryAttempts, exception.Message);
                    });

            _readRetryPolicy = Policy<Update>
                .Handle<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: _maxRetryAttempts,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (delegateResult, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(delegateResult.Exception,
                            "TelegramUpdateChannel: ReadAsync failed. Retrying in {TimeSpan} (attempt {RetryAttempt}/{MaxRetries}). Error: {Message}",
                            timeSpan, retryAttempt, _maxRetryAttempts, delegateResult.Exception?.Message);
                    });

            _logger.LogInformation("TelegramUpdateChannel initialized with capacity {Capacity} and FullMode '{FullMode}'.", capacity, options.FullMode);
        }

        public async ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default)
        {
            // اکنون این فراخوانی ممکن است منتظر بماند اگر Channel پر باشد.
            // مدیریت Timeout و انتقال به Hangfire در لایه بالاتر (TelegramBotService) انجام می‌شود.
            await _writeRetryPolicy.ExecuteAsync(async () =>
            {
                await _channel.Writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async ValueTask<Update> ReadAsync(CancellationToken cancellationToken = default)
        {
            return await _readRetryPolicy.ExecuteAsync(async () =>
            {
                return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}