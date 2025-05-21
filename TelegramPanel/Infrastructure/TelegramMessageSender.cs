// File: TelegramPanel/Infrastructure/TelegramMessageSender.cs
using Application.Common.Interfaces; // برای INotificationJobScheduler
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types; // برای InputFile, ChatId, LinkPreviewOptions
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups; // برای IReplyMarkup, InlineKeyboardMarkup

namespace TelegramPanel.Infrastructure
{
    // =========================================================================
    // 1. اینترفیس برای سرویسی که واقعاً با API تلگرام صحبت می‌کند
    //    این اینترفیس توسط ActualTelegramMessageActions پیاده‌سازی می‌شود
    //    و جاب‌های Hangfire این متدها را فراخوانی می‌کنند.
    // =========================================================================
    public interface IActualTelegramMessageActions
    {
        Task SendTextMessageToTelegramAsync(long chatId, string text, ParseMode? parseMode, ReplyMarkup? replyMarkup, bool disableNotification, LinkPreviewOptions? linkPreviewOptions, CancellationToken cancellationToken);
        Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken);
        Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken);
        Task SendPhotoToTelegramAsync(long chatId, string photoUrlOrFileId, string? caption, ParseMode? parseMode, ReplyMarkup? replyMarkup, CancellationToken cancellationToken);
    }

    // =========================================================================
    // 2. پیاده‌سازی سرویسی که واقعاً با API تلگرام صحبت می‌کند
    //    این کلاس IActualTelegramMessageActions را پیاده‌سازی می‌کند.
    // =========================================================================
    public class ActualTelegramMessageActions : IActualTelegramMessageActions
    {
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<ActualTelegramMessageActions> _logger;
        private const ParseMode DefaultParseMode = ParseMode.Markdown;
        public ActualTelegramMessageActions(ITelegramBotClient botClient, ILogger<ActualTelegramMessageActions> logger)
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SendTextMessageToTelegramAsync(long chatId, string text, ParseMode? parseMode, ReplyMarkup? replyMarkup, bool disableNotification, LinkPreviewOptions? linkPreviewOptions, CancellationToken cancellationToken)
        {
            string logText = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
            _logger.LogDebug("Hangfire Job (ActualSend): Sending text message. ChatID: {ChatId}, Text (partial): '{LogText}'", chatId, logText);
            try
            {
                await _botClient.SendMessage(
                    chatId: new ChatId(chatId),
                    text: text,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: replyMarkup,
                    disableNotification: disableNotification,
                    linkPreviewOptions: linkPreviewOptions,
                    cancellationToken: cancellationToken);
                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent text message to ChatID {ChatId}.", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error sending text message to ChatID {ChatId}. Text (partial): '{LogText}'", chatId, logText);
                throw;
            }
        }




        public async Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Hangfire Job (ActualSend): Editing message. ChatID: {ChatId}, MessageID: {MessageId}", chatId, messageId);
            try
            {
                await _botClient.EditMessageText(
                    chatId: new ChatId(chatId),
                    messageId: messageId,
                    text: text,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken);
                _logger.LogInformation("Hangfire Job (ActualSend): Successfully edited message for ChatID {ChatId}, MessageID: {MessageId}", chatId, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error editing message. ChatID: {ChatId}, MessageID: {MessageId}", chatId, messageId);
                throw;
            }
        }




        public async Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Hangfire Job (ActualSend): Answering CBQ. ID: {CBQId}", callbackQueryId);
            try
            {
                await _botClient.AnswerCallbackQuery(
                    callbackQueryId: callbackQueryId,
                    text: text,
                    showAlert: showAlert,
                    url: url,
                    cacheTime: cacheTime,
                    cancellationToken: cancellationToken);
                _logger.LogInformation("Hangfire Job (ActualSend): Successfully answered CBQ. ID: {CBQId}", callbackQueryId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error answering CBQ. ID: {CBQId}", callbackQueryId);
                throw;
            }
        }



        public async Task SendPhotoToTelegramAsync(long chatId, string photoUrlOrFileId, string? caption, ParseMode? parseMode, ReplyMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Hangfire Job (ActualSend): Sending photo. ChatID: {ChatId}", chatId);
            try
            {
                InputFile photoInput = InputFile.FromString(photoUrlOrFileId);
                await _botClient.SendPhoto( // استفاده از SendPhotoAsync
                    chatId: new ChatId(chatId),
                    photo: photoInput,
                    caption: caption,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken);
                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent photo to ChatID {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error sending photo to ChatID {ChatId}", chatId);
                throw;
            }
        }
    }



    // =========================================================================
    // 3. اینترفیس ITelegramMessageSender
    //    این اینترفیس توسط سایر بخش‌های برنامه (مانند CommandHandler ها) استفاده می‌شود.
    //    پیاده‌سازی آن (HangfireRelayTelegramMessageSender) جاب‌ها را انکیو می‌کند.
    // =========================================================================
    public interface ITelegramMessageSender
    {
        Task SendPhotoAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption = null,
            ParseMode? parseMode = null,
            ReplyMarkup? replyMarkup = null, //  << استفاده از IReplyMarkup
            CancellationToken cancellationToken = default);

        Task SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown,
            ReplyMarkup? replyMarkup = null, //  << استفاده از IReplyMarkup
            CancellationToken cancellationToken = default,
            LinkPreviewOptions? linkPreviewOptions = null);

        Task EditMessageTextAsync(
            long chatId,
            int messageId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown,
            InlineKeyboardMarkup? replyMarkup = null, //  << EditMessageTextAsync مستقیماً InlineKeyboardMarkup می‌گیرد
            CancellationToken cancellationToken = default);

        Task AnswerCallbackQueryAsync(
            string callbackQueryId,
            string? text = null,
            bool showAlert = false,
            string? url = null,
            int cacheTime = 0,
            CancellationToken cancellationToken = default);
    }

    // =========================================================================
    // 4. پیاده‌سازی ITelegramMessageSender که جاب‌ها را به Hangfire "رله" می‌کند
    // =========================================================================
    public class HangfireRelayTelegramMessageSender : ITelegramMessageSender
    {
        private readonly INotificationJobScheduler _jobScheduler;
        private readonly ILogger<HangfireRelayTelegramMessageSender> _logger;

        public HangfireRelayTelegramMessageSender(
            INotificationJobScheduler jobScheduler,
            ILogger<HangfireRelayTelegramMessageSender> logger)
        {
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task SendTextMessageAsync(long chatId, string text, ParseMode? parseMode = ParseMode.Markdown, ReplyMarkup? replyMarkup = null, CancellationToken cancellationToken = default, LinkPreviewOptions? linkPreviewOptions = null)
        {
            _logger.LogDebug("Enqueueing SendTextMessageAsync for ChatID {ChatId}", chatId);
            _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                sender => sender.SendTextMessageToTelegramAsync(chatId, text, parseMode, replyMarkup, false, linkPreviewOptions, CancellationToken.None)
            );
            return Task.CompletedTask;
        }

        public Task EditMessageTextAsync(long chatId, int messageId, string text, ParseMode? parseMode = ParseMode.Markdown, InlineKeyboardMarkup? replyMarkup = null, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Enqueueing EditMessageTextAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);
            _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                sender => sender.EditMessageTextInTelegramAsync(chatId, messageId, text, parseMode, replyMarkup, CancellationToken.None)
            );
            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, string? url = null, int cacheTime = 0, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Enqueueing AnswerCallbackQueryAsync for CBQID {CallbackQueryId}", callbackQueryId);
            _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                sender => sender.AnswerCallbackQueryToTelegramAsync(callbackQueryId, text, showAlert, url, cacheTime, CancellationToken.None)
            );
            return Task.CompletedTask;
        }

        public Task SendPhotoAsync(long chatId, string photoUrlOrFileId, string? caption = null, ParseMode? parseMode = null, ReplyMarkup? replyMarkup = null, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Enqueueing SendPhotoAsync for ChatID {ChatId}", chatId);
            _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                sender => sender.SendPhotoToTelegramAsync(chatId, photoUrlOrFileId, caption, parseMode, replyMarkup, CancellationToken.None)
            );
            return Task.CompletedTask;
        }
    }
}