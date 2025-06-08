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
            IDirectMessageSender directMessageSender)
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
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            // اطلاعات آپدیت و UserID در Log Scope ای که توسط UpdateQueueConsumerService ایجاد شده، قابل دسترس خواهد بود.
            // ✅ مهم: این متد (ProcessUpdateAsync) از قبل روی یک Thread Pool Thread در حال اجرا است.
            // نیازی به Task.Run اضافی در اینجا نیست.
            _logger.LogInformation("Beginning pipeline processing for update ID: {UpdateId}.", update.Id);

            // تعریف نقطه پایانی (داخلی‌ترین بخش) پایپ‌لاین.
            // این delegate زمانی اجرا می‌شود که آپدیت از تمام Middleware ها عبور کرده باشد.
            // وظیفه آن مسیریابی آپدیت پردازش‌شده به ماشین وضعیت یا Command Handler مناسب است.
            TelegramPipelineDelegate finalHandlerAction = async (processedUpdate, ct) =>
            {
                await RouteToHandlerOrStateMachineAsync(processedUpdate, ct);
            };

            // ساخت پایپ‌لاین Middleware ها با استفاده از Aggregate.
            var pipeline = _middlewares.Aggregate(
                finalHandlerAction,
                (nextMiddlewareInChain, currentMiddleware) =>
                    async (upd, ct) => await currentMiddleware.InvokeAsync(upd, nextMiddlewareInChain, ct)
            );

            try
            {
                // اجرای کل پایپ‌لاین با آپدیت ورودی.
                // این فراخوانی، اجرای اولین Middleware در زنجیره را آغاز می‌کند.
                // ✅ این 'await' بلاک‌کننده نیست. در حین انتظار برای عملیات I/O (در Middlewareها یا Handlerها)،
                // Thread به ThreadPool بازگردانده می‌شود.
                await pipeline(update, cancellationToken);
                _logger.LogInformation("Pipeline processing completed successfully for update ID: {UpdateId}.", update.Id);
            }
            catch (Exception ex)
            {
                // این catch block برای خطاهایی است که در طول اجرای پایپ‌لاین (توسط Middleware ها یا Handler ها) مدیریت نشده و به اینجا رسیده‌اند.
                _logger.LogError(ex, "An unhandled exception escaped the Telegram update processing pipeline for update ID: {UpdateId}.", update.Id);
                // تلاش برای ارسال پیام خطای عمومی به کاربر.
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
        private async Task RouteToHandlerOrStateMachineAsync(Update update, CancellationToken cancellationToken)
        {
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            if (!userId.HasValue)
            {
                _logger.LogWarning("Cannot route Update ID: {UpdateId}. UserID is missing from the update object.", update.Id);
                return;
            }

            // اطلاعات برای Context مربوط به Polly.
            var pollyContext = new Polly.Context($"RouteToHandler_{update.Id}", new Dictionary<string, object>
            {
                { "UpdateId", update.Id },
                { "TelegramUserId", userId.Value }
            });

            // Priority 1: Check and process with the State Machine
            ITelegramState? currentState = null;
            try
            {
                // ✅ اعمال سیاست تلاش مجدد بر روی GetCurrentStateAsync
                await _internalServiceRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    // این فراخوانی باید خودش async باشد و Thread را بلاک نکند.
                    currentState = await _stateMachine.GetCurrentStateAsync(userId.Value, ct);
                }, pollyContext, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving current state for UserID {UserId} while processing Update ID: {UpdateId} after retries.", userId.Value, update.Id);
                await HandleProcessingErrorAsync(update, ex, cancellationToken); // Notify user of the error
                return; // Cannot proceed reliably if state retrieval fails.
            }

            if (currentState != null)
            {
                _logger.LogInformation("UserID {UserId} is in state '{StateName}'. Processing UpdateID {UpdateId} with state machine.",
                    userId.Value, currentState.Name, update.Id);
                try
                {
                    // ✅ اعمال سیاست تلاش مجدد بر روی ProcessUpdateInCurrentStateAsync
                    await _internalServiceRetryPolicy.ExecuteAsync(async (context, ct) =>
                    {
                        // این فراخوانی نیز باید خودش async باشد و Thread را بلاک نکند.
                        await _stateMachine.ProcessUpdateInCurrentStateAsync(userId.Value, update, ct);
                    }, pollyContext, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing UpdateID {UpdateId} in state '{StateName}' for UserID {UserId} after retries.",
                        update.Id, currentState.Name, userId.Value);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken); // Notify user of the error
                                                                                     // Clear the user's state on error to prevent getting stuck in a faulty state.
                    try
                    {
                        // ✅ اعمال سیاست تلاش مجدد بر روی ClearStateAsync
                        await _internalServiceRetryPolicy.ExecuteAsync(async (context, ct) =>
                        {
                            // این فراخوانی نیز باید خودش async باشد و Thread را بلاک نکند.
                            await _stateMachine.ClearStateAsync(userId.Value, ct);
                        }, pollyContext, CancellationToken.None); // CancellationToken.None برای اطمینان از پاکسازی وضعیت
                    }
                    catch (Exception clearEx)
                    {
                        _logger.LogError(clearEx, "Critical: Failed to clear state for UserID {UserId} after processing error in state '{StateName}'.", userId.Value, currentState.Name);
                    }
                    _logger.LogInformation("State cleared for UserID {UserId} due to processing error in state '{StateName}'.", userId.Value, currentState.Name);
                }
                return; // If the state machine was active, it's considered to have handled (or attempted to handle) the update.
            }

            // Priority 2: If not in a specific state, route based on UpdateType
            _logger.LogDebug("No active state for UserID {UserId}. Routing UpdateID {UpdateId} by type: {UpdateType}", userId.Value, update.Id, update.Type);

            bool handledByTypeSpecificHandler = false;

            if (update.Type == UpdateType.Message && update.Message != null)
            {
                ITelegramCommandHandler? commandHandler = null;
                try
                {
                    // Find the first command handler that can process this message update.
                    commandHandler = _commandHandlers.FirstOrDefault(h => h.CanHandle(update));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while trying to find a suitable ITelegramCommandHandler for UpdateID {UpdateId}.", update.Id);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken);
                    return;
                }

                if (commandHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} (Type: Message) to ITelegramCommandHandler: {HandlerName}", update.Id, commandHandler.GetType().Name);
                    try
                    {
                        // ✅ مهم: HandleAsync خود Handler باید async باشد و Thread را بلاک نکند.
                        await commandHandler.HandleAsync(update, cancellationToken);
                        handledByTypeSpecificHandler = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing ITelegramCommandHandler {HandlerName} for UpdateID {UpdateId}.", commandHandler.GetType().Name, update.Id);
                        await HandleProcessingErrorAsync(update, ex, cancellationToken);
                    }
                }
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                ITelegramCallbackQueryHandler? callbackHandler = null;
                try
                {
                    _logger.LogDebug("Searching for ITelegramCallbackQueryHandler for CBQ Data: '{CBQData}'. Available handlers: {HandlerCount}",
                        update.CallbackQuery.Data, _callbackQueryHandlers.Count());

                    foreach (var h_instance in _callbackQueryHandlers)
                    {
                        bool canItHandle = h_instance.CanHandle(update);
                        _logger.LogTrace("Checking ITelegramCallbackQueryHandler: {HandlerType}. CanHandle for '{CBQData}'? -> {CanHandleResult}",
                            h_instance.GetType().FullName, update.CallbackQuery.Data, canItHandle);
                        if (canItHandle)
                        {
                            callbackHandler = h_instance;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error finding ITelegramCallbackQueryHandler for UpdateID {UpdateId}, CBQData: {CBQData}", update.Id, update.CallbackQuery.Data);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken);
                    return;
                }

                if (callbackHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} (Type: CallbackQuery, Data: '{CBQData}') to ITelegramCallbackQueryHandler: {HandlerName}",
                        update.Id, update.CallbackQuery.Data, callbackHandler.GetType().Name);
                    try
                    {
                        // ✅ مهم: HandleAsync خود Handler باید async باشد و Thread را بلاک نکند.
                        await callbackHandler.HandleAsync(update, cancellationToken);
                        handledByTypeSpecificHandler = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing ITelegramCallbackQueryHandler {HandlerName} for UpdateID {UpdateId}, CBQData: {CBQData}",
                            callbackHandler.GetType().Name, update.Id, update.CallbackQuery.Data);
                        await HandleProcessingErrorAsync(update, ex, cancellationToken);
                    }
                }
            }
            // Add other UpdateType handlers (e.g., EditedMessage, Poll, etc.) here if needed.

            if (!handledByTypeSpecificHandler)
            {
                string contentPartial = (update.Message?.Text ?? update.CallbackQuery?.Data ?? update.InlineQuery?.Query ?? "N/A");
                if (contentPartial.Length > 50) contentPartial = contentPartial.Substring(0, 50) + "...";

                _logger.LogWarning("No suitable specific handler (Command or CallbackQuery) or active state found for Update ID: {UpdateId}. UpdateType: {UpdateType}. Content(partial): '{Content}'. Routing to unknown/unmatched handler.",
                    update.Id, update.Type, contentPartial);
                await HandleUnknownOrUnmatchedUpdateAsync(update, cancellationToken);
            }
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
            try
            {
                // STEP 1: Send the message DIRECTLY and get its ID
                var sentMessage = await _directMessageSender.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Sorry, I didn't understand that command. This message will self-destruct.",
                    cancellationToken: cancellationToken);

                // If the message failed to send (e.g., user blocked bot), stop here.
                if (sentMessage is null)
                {
                    _logger.LogWarning("Failed to send ephemeral message to chat {ChatId}, it might be blocked.", chatId);
                    return;
                }

                // STEP 2: Wait for 3 seconds
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

                // STEP 3: Delete the message DIRECTLY
                await _directMessageSender.DeleteMessageAsync(
                    chatId: chatId,
                    messageId: sentMessage.MessageId,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // This is expected if the application is shutting down. Safe to ignore.
            }
            catch (Exception ex)
            {
                // CRITICAL: Catch all other exceptions. An unhandled exception in a fire-and-forget
                // task will crash the entire application process.
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