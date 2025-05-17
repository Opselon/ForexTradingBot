// File: TelegramPanel/Infrastructure/TelegramNotificationService.cs
#region Usings
using Application.Common.Interfaces; // ✅ برای INotificationService (از پروژه Application)
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types.Enums;   // ✅ برای ParseMode
using TelegramPanel.Formatters;   // ✅ برای TelegramMessageFormatter
// ITelegramMessageSender باید در همین namespace یا یک using صحیح داشته باشد.
// فرض می‌کنیم ITelegramMessageSender در TelegramPanel.Infrastructure تعریف شده.
#endregion

namespace TelegramPanel.Infrastructure // ✅ Namespace: TelegramPanel.Infrastructure
{
    public class TelegramNotificationService : INotificationService
    {
        #region Private Readonly Fields
        private readonly ITelegramMessageSender _telegramMessageSender;
        private readonly ILogger<TelegramNotificationService> _logger;
        #endregion

        #region Constructor
        public TelegramNotificationService(
            ITelegramMessageSender telegramMessageSender,
            ILogger<TelegramNotificationService> logger)
        {
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        #endregion

        #region INotificationService Implementation
        public async Task SendNotificationAsync(string recipientIdentifier, string message, bool useRichText = false, CancellationToken cancellationToken = default)
        {
            // recipientIdentifier در اینجا همان Telegram User ID (به صورت رشته) است.
            if (!long.TryParse(recipientIdentifier, out long chatId))
            {
                _logger.LogError("Invalid recipient identifier for Telegram notification: {RecipientIdentifier}. Expected a long ChatID.", recipientIdentifier);
                return; // یا throw exception
            }

            ParseMode? parseMode = useRichText ? ParseMode.MarkdownV2 : null;
            string messageToSend = message;

            // اگر از MarkdownV2 استفاده می‌کنیم، و فرمتتر ما مسئول escape کردن است، اینجا باید آن را فراخوانی کنیم.
            // اما اگر پیام از لایه Application از قبل با فرمت MarkdownV2 آماده شده، نیازی به escape مجدد نیست.
            // فرض فعلی: پیام ورودی 'message' اگر useRichText=true باشد، از قبل برای MarkdownV2 آماده است.
            // اگر نه، TelegramMessageFormatter باید توابع Escape هم داشته باشد.
            // مثال:
            // if (useRichText && parseMode == ParseMode.MarkdownV2)
            // {
            //     messageToSend = TelegramMessageFormatter.EscapeMarkdownV2(message); // اگر TelegramMessageFormatter این متد را دارد
            // }


            _logger.LogInformation("Sending Telegram notification to ChatID {ChatId}. RichText: {UseRichText}, Message (partial): {MessagePartial}",
                chatId, useRichText, message.Length > 50 ? message.Substring(0, 50) + "..." : message);

            try
            {
                await _telegramMessageSender.SendTextMessageAsync(chatId, messageToSend, parseMode, null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Telegram notification to ChatID {ChatId}.", chatId);
                // می‌توانید خطا را دوباره throw کنید اگر لایه بالاتر باید از آن مطلع شود.
            }
        }
        #endregion
    }
}