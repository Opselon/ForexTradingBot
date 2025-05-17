using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// namespace TelegramPanel.Infrastructure; // این خط اضافی بود یا در جای نادرست

namespace TelegramPanel.Infrastructure // ✅ namespace باید اینجا شروع شود و تمام کلاس‌ها و اینترفیس‌های این فایل را در بر بگیرد
{
    public interface ITelegramMessageSender
    {
        // ✅ اطمینان از اینکه IReplyMarkup از namespace صحیح Telegram.Bot.Types.ReplyMarkups است
        Task SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown,
            ReplyMarkup? replyMarkup = null, // ✅ این نوع باید با پارامتر SendTextMessageAsync کتابخانه مطابقت داشته باشد
            CancellationToken cancellationToken = default);
    }

    public class TelegramMessageSender : ITelegramMessageSender
    {
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<TelegramMessageSender> _logger;

        public TelegramMessageSender(ITelegramBotClient botClient, ILogger<TelegramMessageSender> logger)
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown, // ✅ پارامتر parseMode دریافت می‌شود
            ReplyMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string logText = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
                _logger.LogDebug("Attempting to send text message to ChatID {ChatId}. Text (partial): '{Text}'", chatId, logText);

                await _botClient.SendMessage( // ✅ استفاده از نام متد صحیح
                    chatId: new ChatId(chatId),
                    text: text,
                    replyMarkup: replyMarkup,
                    disableNotification: false, // مثال برای پارامترهای دیگر (اختیاری)
                    protectContent: false,      // مثال برای پارامترهای دیگر (اختیاری)
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully sent text message to ChatID {ChatId}. Text (partial): '{Text}'", chatId, logText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending text message to ChatID {ChatId}. Text (partial): '{Text}'", chatId, text.Length > 100 ? text.Substring(0, 100) + "..." : text);
                // می‌توانید خطا را دوباره throw کنید یا مدیریت کنید
                // throw; // اگر می‌خواهید لایه بالاتر خطا را مدیریت کند
            }
        }
    }
    }