using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums; // Required for UpdateType
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.Pipeline; // برای TelegramPipelineDelegate
using TelegramPanel.Infrastructure.Services; // فرض شده ITelegramMessageSender در اینجا پیاده‌سازی شده
using Polly; // اضافه شده برای Polly
using Polly.Retry; // اضافه شده برای سیاست‌های Retry
using System;
using System.Collections.Generic; // برای IReadOnlyList
using System.Linq; // برای Reverse(), FirstOrDefault(), Any()
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Exceptions;
using Microsoft.Extensions.Caching.Memory;

namespace TelegramPanel.Infrastructure // یا Application اگر در آن لایه است
{
    /// <summary>
    /// سرویس اصلی برای پردازش آپدیت‌های دریافتی تلگرام.
    /// این سرویس یک پایپ‌لاین از Middleware ها را اجرا کرده و سپس آپدیت را به
    /// ماشین وضعیت (<see cref="ITelegramStateMachine"/>) یا یک Command Handler مناسب (<see cref="ITelegramCommandHandler"/>) مسیریابی می‌کند.
    /// از Polly برای افزایش پایداری در برابر خطاهای گذرا در تعاملات با سرویس‌های داخلی و خارجی استفاده می‌کند.
    /// </summary>
    public class UpdateProcessingService : ITelegramUpdateProcessor
    {
        #region Fields

        private readonly ILogger<UpdateProcessingService> _logger;
        private readonly IServiceProvider _serviceProvider; // برای Resolve کردن سرویس‌ها در Scope های داخلی
        private readonly IReadOnlyList<ITelegramMiddleware> _middlewares;
        private readonly IEnumerable<ITelegramCommandHandler> _commandHandlers;
        private readonly IEnumerable<ITelegramCallbackQueryHandler> _callbackQueryHandlers;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IDirectMessageSender _directMessageSender; // <-- INJECT THE NEW SENDER
        private readonly AsyncRetryPolicy _internalServiceRetryPolicy; // سیاست Polly برای سرویس‌های داخلی/DB
        private readonly AsyncRetryPolicy _externalApiRetryPolicy;    // سیاست Polly برای فراخوانی‌های API خارجی
        private readonly IMemoryCache _memoryCache; // ✅ INJECT THE MEMORY CACHE
        private readonly IEnumerable<ITelegramCallbackQueryHandler> _callbackHandlers;
        #endregion

        #region Constructor

        public UpdateProcessingService(
            ILogger<UpdateProcessingService> logger,
            IServiceProvider serviceProvider,
            IEnumerable<ITelegramMiddleware> middlewares,
            IEnumerable<ITelegramCommandHandler> commandHandlers,
            IEnumerable<ITelegramCallbackQueryHandler> callbackQueryHandlers,
            ITelegramStateMachine stateMachine,
            ITelegramMessageSender messageSender,
            IDirectMessageSender directMessageSender,
            IMemoryCache memoryCache)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            // Middleware ها را Reverse کرده و به عنوان ReadOnlyList ذخیره می‌کند تا پایپ‌لاین به درستی ساخته شود.
            _middlewares = middlewares?.Reverse().ToList().AsReadOnly() ?? throw new ArgumentNullException(nameof(middlewares));
            _commandHandlers = commandHandlers ?? throw new ArgumentNullException(nameof(commandHandlers));
            _callbackQueryHandlers = callbackQueryHandlers ?? throw new ArgumentNullException(nameof(callbackQueryHandlers));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));

            // تعریف _internalServiceRetryPolicy برای عملیات‌های داخلی (مانند دسترسی به DB از طریق StateMachine)
            _internalServiceRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var updateId = context.TryGetValue("UpdateId", out var id) ? (int?)id : null;
                        var userId = context.TryGetValue("TelegramUserId", out var uid) ? (long?)uid : null;
                        _logger.LogWarning(exception,
                            "PollyRetry (InternalService): Operation failed for UpdateId {UpdateId}, UserId {UserId}. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            updateId, userId, timeSpan, retryAttempt, exception.Message);
                    });

            // تعریف _externalApiRetryPolicy برای فراخوانی‌های API خارجی (مانند ارسال پیام تلگرام)
            _externalApiRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var updateId = context.TryGetValue("UpdateId", out var id) ? (int?)id : null;
                        var chatId = context.TryGetValue("ChatId", out var cid) ? (long?)cid : null;
                        _logger.LogWarning(exception,
                            "PollyRetry (ExternalAPI): API call failed for UpdateId {UpdateId}, ChatId {ChatId}. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            updateId, chatId, timeSpan, retryAttempt, exception.Message);
                    });
            _directMessageSender = directMessageSender;
            _memoryCache = memoryCache;
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// آپدیت تلگرام را با عبور از پایپ‌لاین Middleware ها پردازش کرده و به Handler مناسب ارسال می‌کند.
        /// </summary>
        /// <param name="update">آپدیت دریافت شده از تلگرام.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        public async Task ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Beginning pipeline processing for update ID: {UpdateId}.", update.Id);

            TelegramPipelineDelegate finalHandlerAction = async (processedUpdate, ct) =>
            {
                await RouteToHandlerOrStateMachineAsync(processedUpdate, ct);
            };

            var pipeline = _middlewares.Aggregate(
                finalHandlerAction,
                (nextMiddlewareInChain, currentMiddleware) =>
                    async (upd, ct) => await currentMiddleware.InvokeAsync(upd, nextMiddlewareInChain, ct)
            );

            try
            {
                await pipeline(update, cancellationToken);
                _logger.LogInformation("Pipeline processing completed successfully for update ID: {UpdateId}.", update.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception escaped the Telegram update processing pipeline for update ID: {UpdateId}.", update.Id);
                await HandleProcessingErrorAsync(update, ex, cancellationToken);
            }
        }


        #endregion

        #region Private Helper Methods

        /// <summary>
        /// آپدیت را به ماشین وضعیت (اگر کاربر در وضعیتی باشد) یا به یک Command Handler مناسب مسیریابی می‌کند.
        /// این متد از سیاست تلاش مجدد برای تعامل با <see cref="ITelegramStateMachine"/> استفاده می‌کند.
        /// </summary>
        /// <param name="update">آپدیت پردازش شده توسط پایپ‌لاین Middleware.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        // This method goes inside your UpdateProcessingService.cs

        /// <summary>
        /// Routes an update to the correct handler based on a defined priority,
        /// wrapping key interactions with a Polly retry policy for resilience.
        /// Priority Order:
        /// 1. Specific Command Handlers (e.g., /start, /cancel)
        /// 2. Specific Callback Query Handlers (for button clicks)
        /// 3. Active State Machine (if the user is in a conversation)
        /// 4. Fallback for unknown updates.
        /// </summary>
        private async Task RouteToHandlerOrStateMachineAsync(Update update, CancellationToken cancellationToken)
        {
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            if (!userId.HasValue)
            {
                _logger.LogWarning("Cannot route Update ID: {UpdateId}. UserID is missing from the update object.", update.Id);
                return;
            }

            var pollyContext = new Polly.Context($"RouteToHandler_{update.Id}", new Dictionary<string, object>
    {
        { "UpdateId", update.Id },
        { "TelegramUserId", userId.Value }
    });

            // =========================================================================
            // FIX: THE ROUTING LOGIC IS REORDERED HERE
            // =========================================================================

            // --- Priority 1: Check for a specific Command or Callback Handler ---
            bool handledByTypeSpecificHandler = false;

            // Check for Commands (e.g., /start, /cancel)
            if (update.Type == UpdateType.Message && update.Message?.Text?.StartsWith('/') == true)
            {
                ITelegramCommandHandler? commandHandler = _commandHandlers.FirstOrDefault(h => h.CanHandle(update));
                if (commandHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} to CommandHandler: {HandlerName}", update.Id, commandHandler.GetType().Name);
                    await commandHandler.HandleAsync(update, cancellationToken);
                    handledByTypeSpecificHandler = true;
                }
            }
            // Check for Callback Queries (Button Clicks)
            else if (update.Type == UpdateType.CallbackQuery)
            {
                ITelegramCallbackQueryHandler? callbackHandler = _callbackQueryHandlers.FirstOrDefault(h => h.CanHandle(update));
                if (callbackHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} to CallbackQueryHandler: {HandlerName} for data '{CallbackData}'",
                        update.Id, callbackHandler.GetType().Name, update.CallbackQuery!.Data);
                    await callbackHandler.HandleAsync(update, cancellationToken);
                    handledByTypeSpecificHandler = true;
                }
            }

            // If a specific handler was found and executed, we are done.
            if (handledByTypeSpecificHandler)
            {
                return;
            }

            // --- Priority 2: If no specific handler was found, THEN check the State Machine ---
            _logger.LogDebug("No specific handler found. Checking for active state for UserID {UserId}.", userId.Value);

            ITelegramState? currentState = null;
            try
            {
                await _internalServiceRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    currentState = await _stateMachine.GetCurrentStateAsync(userId.Value, ct);
                }, pollyContext, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving current state for UserID {UserId} while processing Update ID: {UpdateId} after retries.", userId.Value, update.Id);
                await HandleProcessingErrorAsync(update, ex, cancellationToken);
                return;
            }

            if (currentState != null)
            {
                _logger.LogInformation("UserID {UserId} is in state '{StateName}'. Processing UpdateID {UpdateId} with state machine.",
                    userId.Value, currentState.Name, update.Id);
                try
                {
                    await _internalServiceRetryPolicy.ExecuteAsync(async (context, ct) =>
                    {
                        await _stateMachine.ProcessUpdateInCurrentStateAsync(userId.Value, update, ct);
                    }, pollyContext, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing UpdateID {UpdateId} in state '{StateName}' for UserID {UserId} after retries.",
                        update.Id, currentState.Name, userId.Value);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken);

                    try
                    {
                        _logger.LogWarning("Attempting to clear faulty state '{StateName}' for UserID {UserId}.", currentState.Name, userId.Value);
                        await _internalServiceRetryPolicy.ExecuteAsync(async (context, ct) =>
                        {
                            await _stateMachine.ClearStateAsync(userId.Value, ct);
                        }, pollyContext, CancellationToken.None);
                        _logger.LogInformation("State cleared for UserID {UserId} due to processing error.", userId.Value);
                    }
                    catch (Exception clearEx)
                    {
                        _logger.LogError(clearEx, "CRITICAL: Failed to clear state for UserID {UserId} after processing error in state '{StateName}'.", userId.Value, currentState.Name);
                    }
                }
                return;
            }

            // --- Fallback: No handler or state found ---
            var contentPartial = (update.Message?.Text ?? update.CallbackQuery?.Data ?? update.InlineQuery?.Query ?? "N/A");
            if (contentPartial.Length > 50) contentPartial = contentPartial.Substring(0, 50) + "...";

            _logger.LogWarning("No suitable specific handler or active state found for Update ID: {UpdateId}. UpdateType: {UpdateType}. Content(partial): '{Content}'. Routing to unknown/unmatched handler.",
                update.Id, update.Type, contentPartial);
            await HandleUnknownOrUnmatchedUpdateAsync(update, cancellationToken);
        }
        /// <summary>
        /// Handles updates that were not managed by any Command Handler or active state.
        /// It sends a default message to the user which self-destructs after a few seconds.
        /// This method uses the <see cref="_externalApiRetryPolicy"/> retry policy for sending and deleting the message.
        /// </summary>
        /// <param name="update">The unhandled or unmatched update.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <summary>
        /// Handles updates that were not managed by any other handler.
        /// This method immediately returns while launching a background task to handle the response.
        /// </summary>
        private Task HandleUnknownOrUnmatchedUpdateAsync(Update update, CancellationToken cancellationToken)
        {
            var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;

            if (chatId.HasValue)
            {
                // ✅ FIRE-AND-FORGET: Start the task but don't wait for it.
                // This allows the handler to return immediately and process the next update.
                _ = SendAndDeleteEphemeralMessageAsync(chatId.Value, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Cannot handle unknown update {UpdateId} because ChatId is missing.", update.Id);
            }

            return Task.CompletedTask;
        }

        private async Task SendAndDeleteEphemeralMessageAsync(long chatId, CancellationToken cancellationToken)
        {
            // --- ANTI-SPAM LOGIC ---
            // Define a unique cache key for this user's rate limit.
            var rateLimitCacheKey = $"unknown_command_ratelimit_{chatId}";

            // Check if a rate-limit entry already exists for this user.
            // The '_' is a discard, we only care IF the value exists, not what it is.
            if (_memoryCache.TryGetValue(rateLimitCacheKey, out _))
            {
                // The user is rate-limited. Log it for debugging and do nothing.
                _logger.LogInformation("Rate limit triggered for ChatId {ChatId}. Suppressing 'unknown command' message.", chatId);
                return; // Exit the method silently.
            }
            // --- END ANTI-SPAM LOGIC ---

            try
            {
                // If not rate-limited, proceed to send the message.
                var sentMessage = await _directMessageSender.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Sorry, I didn't understand that command. This message will self-destruct.",
                    cancellationToken: cancellationToken);

                if (sentMessage is null)
                {
                    _logger.LogWarning("Failed to send ephemeral message to chat {ChatId}, it might be blocked.", chatId);
                    return;
                }

                // --- ANTI-SPAM LOGIC ---
                // After successfully sending, set the rate-limit cache entry for this user.
                // It will automatically expire after 10 seconds.
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromSeconds(10));

                _memoryCache.Set(rateLimitCacheKey, true, cacheEntryOptions);
                // --- END ANTI-SPAM LOGIC ---

                // Wait for 3 seconds to delete the message.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

                await _directMessageSender.DeleteMessageAsync(
                    chatId: chatId,
                    messageId: sentMessage.MessageId,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Safe to ignore.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fire-and-forget task 'SendAndDeleteEphemeralMessageAsync' for chat {ChatId}.", chatId);
            }
        }


        /// <summary>
        /// خطاهای پیش‌بینی نشده در حین پردازش آپدیت را مدیریت می‌کند و یک پیام عمومی خطا به کاربر ارسال می‌کند.
        /// این متد از سیاست تلاش مجدد <see cref="_externalApiRetryPolicy"/> برای ارسال پیام استفاده می‌کند.
        /// </summary>
        /// <param name="update">آپدیت که در حین پردازش آن خطا رخ داده است.</param>
        /// <param name="exception">خطای رخ داده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات ارسال پیام (معمولاً CancellationToken.None استفاده می‌شود تا پیام خطا حتما ارسال شود).</param>
        private async Task HandleProcessingErrorAsync(Update update, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "Handling processing error for update ID: {UpdateId}. Attempting to notify user.", update.Id);
            var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;
            if (chatId.HasValue)
            {
                // اطلاعات برای Context مربوط به Polly
                var pollyContext = new Polly.Context($"ProcessingErrorMessage_{update.Id}", new Dictionary<string, object>
                {
                    { "UpdateId", update.Id },
                    { "ChatId", chatId.Value }
                });

                try
                {
                    // ✅ اعمال سیاست تلاش مجدد بر روی SendTextMessageAsync
                    // از CancellationToken.None استفاده می‌شود تا اطمینان حاصل شود که این پیام خطا حتی اگر درخواست اصلی (و CancellationToken آن)
                    // لغو شده باشد، همچنان شانس ارسال داشته باشد.
                    await _externalApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                    {
                        // این فراخوانی باید async باشد و Thread را بلاک نکند.
                        await _messageSender.SendTextMessageAsync(chatId.Value,
                            "🤖 Oops! Something went wrong while processing your request. Our team has been notified. Please try again in a moment.",
                            cancellationToken: CancellationToken.None); // در اینجا از CancellationToken.None برای ارسال پیام اضطراری استفاده می‌شود
                    }, pollyContext, CancellationToken.None); // ارسال CancellationToken.None به Polly
                }
                catch (Exception sendEx)
                {
                    // اگر حتی ارسال پیام خطا نیز با مشکل مواجه شود، این یک خطای بحرانی‌تر است.
                    _logger.LogError(sendEx, "Critical: Failed to send error notification message to user {ChatId} for update ID: {UpdateId} after a processing error and retries.", chatId.Value, update.Id);
                }
            }
            else
            {
                _logger.LogWarning("Cannot send error notification for update ID: {UpdateId} as ChatId is missing. Original error: {ExceptionMessage}", update.Id, exception.Message);
            }
        }
        #endregion
    }
}