using System.Threading.Channels;
using Telegram.Bot.Types; // برای Update

namespace TelegramPanel.Queue
{
    /// <summary>
    /// یک صف مبتنی بر Channel برای نگهداری آپدیت‌های دریافتی از تلگرام.
    /// این صف به صورت Singleton رجیستر می‌شود.
    /// </summary>
    public interface ITelegramUpdateChannel
    {
        ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default);
        ValueTask<Update> ReadAsync(CancellationToken cancellationToken = default);
        IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken = default);
    }

    public class TelegramUpdateChannel : ITelegramUpdateChannel
    {
        private readonly Channel<Update> _channel;

        public TelegramUpdateChannel(int capacity = 100) // ظرفیت صف قابل تنظیم است
        {
            // UnboundedChannel: ظرفیت نامحدود (مراقب مصرف حافظه باشید)
            // BoundedChannel: ظرفیت محدود
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait, // اگر صف پر است، منتظر بمان
                SingleReader = false, // اگر چندین Consumer دارید، false باشد (برای ما معمولاً true است)
                SingleWriter = false  // اگر چندین Producer دارید، false باشد (برای Webhook true است)
            };
            _channel = Channel.CreateBounded<Update>(options);
        }

        public async ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default)
        {
            await _channel.Writer.WriteAsync(update, cancellationToken);
        }

        public async ValueTask<Update> ReadAsync(CancellationToken cancellationToken = default)
        {
            return await _channel.Reader.ReadAsync(cancellationToken);
        }

        public IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}