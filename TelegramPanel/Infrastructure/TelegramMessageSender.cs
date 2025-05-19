using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// namespace TelegramPanel.Infrastructure; // این خط اضافی بود یا در جای نادرست

namespace TelegramPanel.Infrastructure // ✅ namespace باید اینجا شروع شود و تمام کلاس‌ها و اینترفیس‌های این فایل را در بر بگیرد
{
    public interface ITelegramMessageSender
    {

        /// <summary>
        /// Sends a photo to a specific Telegram chat.
        /// </summary>
        /// <param name="chatId">Target chat ID.</param>
        /// <param name="photoUrlOrFileId">URL or Telegram FileId of the photo.</param>
        /// <param name="caption">Optional caption for the photo.</param>
        /// <param name="parseMode">Optional parse mode (Markdown/HTML).</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        Task SendPhotoAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption = null,
            ParseMode? parseMode = null,
            ReplyMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default);


        // ✅ اطمینان از اینکه IReplyMarkup از namespace صحیح Telegram.Bot.Types.ReplyMarkups است
        Task SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = null,
            ReplyMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default,
            // bool disableWebPagePreview = false); // 📛 حذف این پارامتر
            LinkPreviewOptions? linkPreviewOptions = null); // ✅ پارامتر جدید و کامل‌تر
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
        public async Task SendPhotoAsync(
          long chatId,
          string photoUrlOrFileId,
          string? caption = null,
          ParseMode? parseMode = null,
          ReplyMarkup? replyMarkup = null,
          CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(photoUrlOrFileId))
            {
                _logger.LogWarning("SendPhotoAsync called with empty photoUrlOrFileId for ChatID {ChatId}.", chatId);
                return; // یا throw ArgumentException
            }

            try
            {
                _logger.LogDebug("Attempting to send photo to ChatID {ChatId}. Photo source: {PhotoSource}", chatId, photoUrlOrFileId);

                // InputFile می‌تواند URL یا FileId باشد. کتابخانه تلگرام خودش تشخیص می‌دهد.
                InputFile photoInput = InputFile.FromString(photoUrlOrFileId);

                await _botClient.SendPhoto(
                    chatId: new ChatId(chatId),
                    photo: photoInput,
                    caption: caption,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully sent photo to ChatID {ChatId}.", chatId);
            }
            catch (ApiRequestException apiEx)
            {
                _logger.LogError(apiEx, "Telegram API error sending photo to ChatID {ChatId}. ErrorCode: {ErrorCode}, Message: {ApiMessage}, PhotoSource: {PhotoSource}",
                    chatId, apiEx.ErrorCode, apiEx.Message, photoUrlOrFileId);
                throw; //  برای مدیریت توسط Polly یا Hangfire
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending photo to ChatID {ChatId}. PhotoSource: {PhotoSource}", chatId, photoUrlOrFileId);
                throw; //  برای مدیریت توسط Polly یا Hangfire
            }
        }
        public async Task SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown, // ✅ پارامتر parseMode دریافت می‌شود
            ReplyMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default,
            LinkPreviewOptions? linkPreviewOptions = null)
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
                    linkPreviewOptions: linkPreviewOptions,
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