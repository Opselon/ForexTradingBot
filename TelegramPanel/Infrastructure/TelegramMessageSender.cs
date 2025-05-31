// File: TelegramPanel/Infrastructure/TelegramMessageSender.cs
using Application.Common.Interfaces; // برای INotificationJobScheduler (و IUserRepository, IAppDbContext)
using Microsoft.Extensions.Logging;
using System;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types; // برای InputFile, ChatId, LinkPreviewOptions
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups; // برای IReplyMarkup, InlineKeyboardMarkup
using Polly; // ✅ اضافه شده برای Polly
using Polly.Retry; // ✅ اضافه شده برای سیاست‌های Retry
using System.Collections.Generic; // برای Dictionary در Context
using System.Threading;
using System.Threading.Tasks;

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
        private const ParseMode DefaultParseMode = ParseMode.Markdown; // این ثابت در کد شما استفاده شده
        private readonly IUserRepository _userRepository;
        private readonly IAppDbContext _context; // ✅ این فیلد در سازنده شما هست، پس باید اینجا هم باشد
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy; // ✅ جدید: سیاست Polly برای API تلگرام

        public ActualTelegramMessageActions(
            ITelegramBotClient botClient,
            ILogger<ActualTelegramMessageActions> logger,
            IUserRepository userRepository,
            IAppDbContext context) // ✅ context در سازنده شما هست.
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _context = context ?? throw new ArgumentNullException(nameof(context)); // ✅ مقداردهی context

            // ✅ تعریف سیاست تلاش مجدد برای فراخوانی‌های Telegram API
            _telegramApiRetryPolicy = Policy
                // مدیریت ApiRequestException (خطاهای خاص تلگرام) و سایر Exceptionها (به جز لغو عملیات)
                .Handle<ApiRequestException>()
                .Or<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                                                                                                            // ✅ اصلاح: پارامتر exception مستقیماً خود Exception است.
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var operationName = context.OperationKey ?? "UnknownOperation";
                        var chatId = context.TryGetValue("ChatId", out var id) ? (long?)id : null;
                        var messagePreview = context.TryGetValue("MessagePreview", out var msg) ? msg?.ToString() : "N/A";
                        var apiErrorCode = (exception as ApiRequestException)?.ErrorCode.ToString() ?? "N/A"; // ✅ صحیح است: استفاده از 'as' برای بررسی نوع

                        _logger.LogWarning(exception, // ✅ صحیح است: 'exception' را مستقیماً به عنوان Exception برای لاگر ارسال کنید.
                            "PollyRetry: Telegram API operation '{Operation}' failed (ChatId: {ChatId}, Code: {ApiErrorCode}). Retrying in {TimeSpan} for attempt {RetryAttempt}. Message preview: '{MessagePreview}'. Error: {Message}",
                            operationName, chatId, apiErrorCode, timeSpan, retryAttempt, messagePreview, exception.Message); // ✅ صحیح است: 'exception.Message'
                    });
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

            // ✅ آماده‌سازی Context برای Polly با استفاده از سازنده‌ای که Dictionary<string, object> می‌گیرد.
            var pollyContext = new Polly.Context($"SendText_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
            {
                { "ChatId", chatId },
                { "MessagePreview", logText }
            });

            try
            {
                // ✅ اعمال سیاست تلاش مجدد Polly
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.SendMessage(
                        chatId: new ChatId(chatId),
                        text: text,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, // ✅ حفظ منطق اصلی شما
                        replyMarkup: replyMarkup,
                        disableNotification: disableNotification,
                        linkPreviewOptions: linkPreviewOptions,
                        cancellationToken: ct); // استفاده از CancellationToken Polly
                }, pollyContext, cancellationToken); // ارسال Context و CancellationToken به Polly

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent text message to ChatID {ChatId}.", chatId);
            }
            catch (ApiRequestException apiEx)
                when (apiEx.ErrorCode == 400 && // Bad Request, often for invalid IDs or blocked bots
                      (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)
                      )
                )
            {
                // این بلاک فقط در صورتی اجرا می‌شود که Polly تمام تلاش‌های مجدد را انجام داده و همچنان با این خطاهای خاص مواجه شده باشد.
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked for ChatID {ChatId} while sending text message after retries. Text (partial): '{LogText}'. Attempting to remove user from local database.", chatId, logText);

                try
                {
                    Domain.Entities.User? userToDelete = await _userRepository.GetByTelegramIdAsync(telegramIdString, cancellationToken);
                    if (userToDelete != null)
                    {
                        await _userRepository.DeleteAndSaveAsync(userToDelete, cancellationToken);
                        _logger.LogInformation("Hangfire Job (ActualSend): Successfully removed user with Telegram ID {TelegramId} (ChatID: {ChatId}) from database due to 'chat not found' or deactivated/blocked status after text message attempt.", userToDelete.TelegramId, chatId);
                    }
                    else
                    {
                        _logger.LogWarning("Hangfire Job (ActualSend): User with Telegram ID {ChatId} was not found in the local database for removal (might have been already removed or never existed) after text message attempt.", chatId);
                    }
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Hangfire Job (ActualSend): Failed to remove user with Telegram ID {ChatId} from database after 'chat not found' error during text message send. The original Telegram error was: {TelegramErrorMessage}", chatId, apiEx.Message);
                }
            }
            catch (Exception ex)
            {
                // این بلاک برای خطاهای دیگری است که Polly نتوانسته آن‌ها را حل کند (پس از تمام تلاش‌های مجدد).
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error sending text message to ChatID {ChatId} after retries. Text (partial): '{LogText}'", chatId, logText);
                throw; // Rethrow other unexpected exceptions for Hangfire to handle (e.g., retry the job again)
            }
        }


        public async Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            string logText = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
            _logger.LogDebug("Hangfire Job (ActualSend): Editing message. ChatID: {ChatId}, MessageID: {MessageId}, Text (partial): '{LogText}'", chatId, messageId, logText);

            // ✅ آماده‌سازی Context برای Polly
            var pollyContext = new Polly.Context($"EditMessage_{chatId}_{messageId}", new Dictionary<string, object>
            {
                { "ChatId", chatId },
                { "MessageId", messageId },
                { "MessagePreview", logText }
            });

            try
            {
                // ✅ اعمال سیاست تلاش مجدد Polly
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.EditMessageText(
                        chatId: new ChatId(chatId),
                        messageId: messageId,
                        text: text,
                        parseMode: ParseMode.Markdown, // ✅ حفظ منطق اصلی شما
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully edited message for ChatID {ChatId}, MessageID: {MessageId}", chatId, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error editing message for ChatID {ChatId}, MessageID: {MessageId} after retries.", chatId, messageId);
                throw;
            }
        }


        public async Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken)
        {
            string logText = text?.Length > 100 ? text.Substring(0, 100) + "..." : text ?? "N/A";
            _logger.LogDebug("Hangfire Job (ActualSend): Answering CBQ. ID: {CBQId}, Text (partial): '{LogText}'", callbackQueryId, logText);

            // ✅ آماده‌سازی Context برای Polly
            var pollyContext = new Polly.Context($"AnswerCBQ_{callbackQueryId}", new Dictionary<string, object>
            {
                { "CallbackQueryId", callbackQueryId },
                { "AnswerTextPreview", logText }
            });

            try
            {
                // ✅ اعمال سیاست تلاش مجدد Polly
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.AnswerCallbackQuery(
                        callbackQueryId: callbackQueryId,
                        text: text,
                        showAlert: showAlert,
                        url: url,
                        cacheTime: cacheTime,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully answered CBQ. ID: {CBQId}", callbackQueryId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error answering CBQ. ID: {CBQId} after retries.", callbackQueryId);
                throw;
            }
        }


        public async Task SendPhotoToTelegramAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption,
            ParseMode? parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken cancellationToken)
        {
            string logCaption = caption?.Length > 100 ? caption.Substring(0, 100) + "..." : caption ?? "N/A";
            _logger.LogDebug("Hangfire Job (ActualSend): Sending photo. ChatID: {ChatId}, Photo: {PhotoIdOrUrl}, Caption (partial): '{LogCaption}'", chatId, photoUrlOrFileId, logCaption);

            // ✅ آماده‌سازی Context برای Polly
            var pollyContext = new Polly.Context($"SendPhoto_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
            {
                { "ChatId", chatId },
                { "PhotoIdOrUrl", photoUrlOrFileId },
                { "CaptionPreview", logCaption }
            });

            try
            {
                InputFile photoInput = InputFile.FromString(photoUrlOrFileId);

                // ✅ اعمال سیاست تلاش مجدد Polly
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.SendPhoto(
                        chatId: new ChatId(chatId),
                        photo: photoInput,
                        caption: caption,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, // ✅ حفظ منطق اصلی شما
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent photo to ChatID {ChatId}", chatId);
            }
            catch (ApiRequestException apiEx)
                when (apiEx.ErrorCode == 400 &&
                      (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)
                      )
                )
            {
                // این بلاک فقط در صورتی اجرا می‌شود که Polly تمام تلاش‌های مجدد را انجام داده و همچنان با این خطاهای خاص مواجه شده باشد.
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked for ChatID {ChatId} while sending photo after retries. Attempting to remove user from local database.", chatId);

                try
                {
                    Domain.Entities.User? userToDelete = await _userRepository.GetByTelegramIdAsync(chatId.ToString(), cancellationToken);
                    if (userToDelete != null)
                    {
                        await _userRepository.DeleteAndSaveAsync(userToDelete, cancellationToken);
                        _logger.LogInformation("Hangfire Job (ActualSend): Successfully removed user with Telegram ID {TelegramId} (ChatID: {ChatId}) from database due to 'chat not found' or deactivated/blocked status.", userToDelete.TelegramId, chatId);
                    }
                    else
                    {
                        _logger.LogWarning("Hangfire Job (ActualSend): User with Telegram ID {ChatId} was not found in the local database for removal (might have been already removed or never existed).", chatId);
                    }
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Hangfire Job (ActualSend): Failed to remove user with Telegram ID {ChatId} from database after 'chat not found' error during photo send. The original Telegram error was: {TelegramErrorMessage}", chatId, apiEx.Message);
                }
            }
            catch (Exception ex)
            {
                // این بلاک برای خطاهای دیگری است که Polly نتوانسته آن‌ها را حل کند (پس از تمام تلاش‌های مجدد).
                _logger.LogError(ex, "Hangfire Job (ActualSend): Unexpected error sending photo to ChatID {ChatId} after retries.", chatId);
                throw;
            }
        }
    }

    // =========================================================================
    // 3. اینترفیس ITelegramMessageSender (بدون تغییر)
    // =========================================================================
    public interface ITelegramMessageSender
    {
        Task SendPhotoAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption = null,
            ParseMode? parseMode = null,
            ReplyMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default);

        Task SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown,
            ReplyMarkup? replyMarkup = null,
            CancellationToken cancellationToken = default,
            LinkPreviewOptions? linkPreviewOptions = null);

        Task EditMessageTextAsync(
            long chatId,
            int messageId,
            string text,
            ParseMode? parseMode = ParseMode.Markdown,
            InlineKeyboardMarkup? replyMarkup = null,
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
    // 4. پیاده‌سازی ITelegramMessageSender که جاب‌ها را به Hangfire "رله" می‌کند (بدون تغییر)
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