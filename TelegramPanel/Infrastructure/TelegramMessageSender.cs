// File: TelegramPanel/Infrastructure/TelegramMessageSender.cs
using Application.Common.Interfaces; // برای INotificationJobScheduler
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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
        private readonly IUserRepository _userRepository; // Inject IUserRepository
        public ActualTelegramMessageActions(ITelegramBotClient botClient, ILogger<ActualTelegramMessageActions> logger, IUserRepository userRepository)
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        }

        public async Task SendTextMessageToTelegramAsync(
    long chatId,
    string text,
    ParseMode? parseMode,
    ReplyMarkup? replyMarkup,
    bool disableNotification,
    LinkPreviewOptions? linkPreviewOptions,
    CancellationToken cancellationToken)
        {
            string logText = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
            _logger.LogDebug("Hangfire Job (ActualSend): Sending text message. ChatID: {ChatId}, Text (partial): '{LogText}'", chatId, logText);

            string telegramIdString = chatId.ToString(); // For repository usage

            try
            {
                await _botClient.SendMessage( // Using SendMessageAsync for consistency
                    chatId: new ChatId(chatId),
                    text: text,
                   parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    replyMarkup: replyMarkup,
                    disableNotification: disableNotification,
                    linkPreviewOptions: linkPreviewOptions,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent text message to ChatID {ChatId}.", chatId);
            }
            catch (ApiRequestException apiEx)
                when (apiEx.ErrorCode == 400 &&
                      (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase) // Another common one
                      )
                )
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked for ChatID {ChatId} while sending text message. Text (partial): '{LogText}'. Attempting to remove user from local database.", chatId, logText);

                try
                {
                    Domain.Entities.User? userToDelete = await _userRepository.GetByTelegramIdAsync(telegramIdString, cancellationToken);
                    if (userToDelete != null)
                    {
                        await _userRepository.DeleteAsync(userToDelete, cancellationToken);
                        _logger.LogInformation("Hangfire Job (ActualSend): Successfully removed user with Telegram ID {TelegramId} (ChatID: {ChatId}) from database due to 'chat not found' or deactivated/blocked status after text message attempt.", userToDelete.TelegramId, chatId);
                    }
                    else
                    {
                        _logger.LogWarning("Hangfire Job (ActualSend): User with Telegram ID {ChatId} was not found in the local database for removal (might have been already removed or never existed) after text message attempt.", chatId);
                    }
                    // The exception is handled, do not rethrow, so Hangfire job can complete.
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Hangfire Job (ActualSend): Failed to remove user with Telegram ID {ChatId} from database after 'chat not found' error during text message send. The original Telegram error was: {TelegramErrorMessage}", chatId, apiEx.Message);
                    // Decide if you want to rethrow the original apiEx or dbEx.
                    // For now, we'll let it complete, as the main task (sending) definitely failed for a known reason.
                    // throw; // Uncomment if you want the job to fail and retry due to DB error.
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error sending text message to ChatID {ChatId}. Text (partial): '{LogText}'", chatId, logText);
                throw; // Rethrow other unexpected exceptions for Hangfire to handle (e.g., retry)
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



        public async Task SendPhotoToTelegramAsync(
         long chatId,
         string photoUrlOrFileId,
         string? caption,
         ParseMode? parseMode , // Consider using this parameter
         ReplyMarkup? replyMarkup,
         CancellationToken cancellationToken)
        {
            string telegramIdString = chatId.ToString(); // For repository usage

            // Optional: Pre-check if user exists in your local DB.
            // This won't prevent "chat not found" from Telegram API if the user
            // blocked the bot or deleted their account after registration,
            // but it can stop attempts if the ID was never valid in your system.
            // However, the primary check should be handling the Telegram API error.
            // For simplicity and to directly address the "chat not found" error handling,
            // we'll focus on the try-catch. If you frequently send to invalid IDs
            // that are NOT in your DB, this pre-check might save some API calls.
            //
            // var userExistsInDb = await _userRepository.ExistsByTelegramIdAsync(telegramIdString, cancellationToken);
            // if (!userExistsInDb)
            // {
            //     _logger.LogWarning("Hangfire Job (ActualSend): Attempted to send photo to Telegram ID {ChatId} which does not exist in our database. Skipping send.", chatId);
            //     return; // Or handle as an error if this state is unexpected
            // }

            _logger.LogDebug("Hangfire Job (ActualSend): Sending photo. ChatID: {ChatId}", chatId);
            try
            {
                InputFile photoInput = InputFile.FromString(photoUrlOrFileId);
                await _botClient.SendPhoto( // Renamed to SendPhotoAsync for consistency
                    chatId: new ChatId(chatId),
                    photo: photoInput,
                    caption: caption,
                    parseMode:Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent photo to ChatID {ChatId}", chatId);
            }
            catch (ApiRequestException apiEx)
                when (apiEx.ErrorCode == 400 &&
                      (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) || // Bot was blocked by the user.
                       apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) || // User account is deactivated
                       apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase) // Another way chat not found can manifest
                      )
                )
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked for ChatID {ChatId}. Attempting to remove user from local database.", chatId);

                try
                {
                    Domain.Entities.User? userToDelete = await _userRepository.GetByTelegramIdAsync(telegramIdString, cancellationToken);
                    if (userToDelete != null)
                    {
                        await _userRepository.DeleteAsync(userToDelete, cancellationToken);
                        _logger.LogInformation("Hangfire Job (ActualSend): Successfully removed user with Telegram ID {TelegramId} (ChatID: {ChatId}) from database due to 'chat not found' or deactivated/blocked status.", userToDelete.TelegramId, chatId);
                    }
                    else
                    {
                        _logger.LogWarning("Hangfire Job (ActualSend): User with Telegram ID {ChatId} was not found in the local database for removal (might have been already removed or never existed).", chatId);
                    }
                    // The exception is handled, do not rethrow, so Hangfire job can complete.
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Hangfire Job (ActualSend): Failed to remove user with Telegram ID {ChatId} from database after 'chat not found' error. The original Telegram error was: {TelegramErrorMessage}", chatId, apiEx.Message);
                    // Decide if you want to rethrow the original apiEx or dbEx.
                    // Rethrowing apiEx would cause Hangfire to retry the send and then the delete again.
                    // Rethrowing dbEx would cause Hangfire to retry based on the DB error.
                    // Not rethrowing means the job might be marked as successful even if DB delete failed,
                    // but the primary issue (chat not found) is acknowledged.
                    // For now, we'll let it complete, as the main task (sending) definitely failed for a known reason.
                    // throw; // Uncomment if you want the job to fail and retry due to DB error.
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Unexpected error sending photo to ChatID {ChatId}", chatId);
                throw; // Rethrow other unexpected exceptions for Hangfire to handle (e.g., retry)
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