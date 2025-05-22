using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums; // Required for Task
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.Pipeline; // برای TelegramPipelineDelegate
using TelegramPanel.Infrastructure.Services;

namespace TelegramPanel.Infrastructure // یا Application اگر در آن لایه است
{
    /// <summary>
    /// سرویس اصلی برای پردازش آپدیت‌های دریافتی تلگرام.
    /// این سرویس یک پایپ‌لاین از Middleware ها را اجرا کرده و سپس آپدیت را به
    /// ماشین وضعیت (<see cref="ITelegramStateMachine"/>) یا یک Command Handler مناسب (<see cref="ITelegramCommandHandler"/>) مسیریابی می‌کند.
    /// </summary>
    public class UpdateProcessingService : ITelegramUpdateProcessor
    {
        #region Fields

        private readonly ILogger<UpdateProcessingService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IReadOnlyList<ITelegramMiddleware> _middlewares;
        private readonly IEnumerable<ITelegramCommandHandler> _commandHandlers;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IEnumerable<ITelegramCallbackQueryHandler> _callbackQueryHandlers;

        public UpdateProcessingService(
            ILogger<UpdateProcessingService> logger,
            IServiceProvider serviceProvider,
            IEnumerable<ITelegramMiddleware> middlewares,
            IEnumerable<ITelegramCommandHandler> commandHandlers,
            IEnumerable<ITelegramCallbackQueryHandler> callbackQueryHandlers,
            ITelegramStateMachine stateMachine,
            ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _middlewares = middlewares?.Reverse().ToList().AsReadOnly() ?? throw new ArgumentNullException(nameof(middlewares));
            _commandHandlers = commandHandlers ?? throw new ArgumentNullException(nameof(commandHandlers));
            _callbackQueryHandlers = callbackQueryHandlers ?? throw new ArgumentNullException(nameof(callbackQueryHandlers));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
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
            _logger.LogInformation("Beginning pipeline processing for update ID: {UpdateId}.", update.Id);

            // تعریف نقطه پایانی (داخلی‌ترین بخش) پایپ‌لاین.
            // این delegate زمانی اجرا می‌شود که آپدیت از تمام Middleware ها عبور کرده باشد.
            // وظیفه آن مسیریابی آپدیت پردازش‌شده به ماشین وضعیت یا Command Handler مناسب است.
            TelegramPipelineDelegate finalHandlerAction = async (processedUpdate, ct) =>
            {
                await RouteToHandlerOrStateMachineAsync(processedUpdate, ct);
            };

            // ساخت پایپ‌لاین Middleware ها با استفاده از Aggregate.
            // پایپ‌لاین از انتها به ابتدا (از `finalHandlerAction` به سمت بیرون) ساخته می‌شود.
            // `_middlewares` قبلاً Reverse شده است، بنابراین اولین middleware در این لیست،
            // اولین middleware ای خواهد بود که آپدیت را پردازش می‌کند.
            // currentMiddleware: Middleware فعلی که در حال اضافه شدن به پایپ‌لاین است.
            // nextMiddlewareInChain: Delegate مربوط به Middleware بعدی در زنجیره (یا finalHandlerAction اگر این آخرین Middleware باشد).
            // نتیجه Aggregate یک delegate واحد است که کل پایپ‌لاین را نمایندگی می‌کند.
            var pipeline = _middlewares.Aggregate(
                finalHandlerAction, // نقطه شروع Aggregation (داخلی‌ترین عمل)
                (nextMiddlewareInChain, currentMiddleware) => // nextMiddlewareInChain نتیجه قبلی Aggregate است (یعنی middleware بعدی یا handler نهایی)
                    async (upd, ct) => await currentMiddleware.InvokeAsync(upd, nextMiddlewareInChain, ct) // currentMiddleware، middleware بعدی را فراخوانی می‌کند
            );

            try
            {
                // اجرای کل پایپ‌لاین با آپدیت ورودی.
                // این فراخوانی، اجرای اولین Middleware در زنجیره را آغاز می‌کند.
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
        /// </summary>
        /// <param name="update">آپدیت پردازش شده توسط پایپ‌لاین Middleware.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        // In UpdateProcessingService.cs

        /// <summary>
        /// Routes the update to the state machine (if the user is in a state) or to an appropriate handler.
        /// </summary>
        /// <param name="update">The update processed by the middleware pipeline.</param>
        /// <param name="cancellationToken">Token for cancellation.</param>
        private async Task RouteToHandlerOrStateMachineAsync(Update update, CancellationToken cancellationToken)
        {
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            if (!userId.HasValue)
            {
                _logger.LogWarning("Cannot route Update ID: {UpdateId}. UserID is missing from the update object.", update.Id);
                return;
            }

            // Priority 1: Check and process with the State Machine
            ITelegramState? currentState = null;
            try
            {
                currentState = await _stateMachine.GetCurrentStateAsync(userId.Value, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving current state for UserID {UserId} while processing Update ID: {UpdateId}.", userId.Value, update.Id);
                await HandleProcessingErrorAsync(update, ex, cancellationToken); // Notify user of the error
                return; // Cannot proceed reliably if state retrieval fails.
            }

            if (currentState != null)
            {
                _logger.LogInformation("UserID {UserId} is in state '{StateName}'. Processing UpdateID {UpdateId} with state machine.",
                    userId.Value, currentState.Name, update.Id);
                try
                {
                    // Process the update using the user's current state logic.
                    await _stateMachine.ProcessUpdateInCurrentStateAsync(userId.Value, update, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing UpdateID {UpdateId} in state '{StateName}' for UserID {UserId}.",
                        update.Id, currentState.Name, userId.Value);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken); // Notify user of the error
                                                                                     // Clear the user's state on error to prevent getting stuck in a faulty state.
                    await _stateMachine.ClearStateAsync(userId.Value, cancellationToken);
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
                    // Order of handlers in _commandHandlers (from DI) can matter if multiple could handle it.
                    commandHandler = _commandHandlers.FirstOrDefault(h => h.CanHandle(update));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while trying to find a suitable ITelegramCommandHandler for UpdateID {UpdateId}.", update.Id);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken);
                    return; // Cannot proceed without a handler if one was expected or resolution failed.
                }

                if (commandHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} (Type: Message) to ITelegramCommandHandler: {HandlerName}", update.Id, commandHandler.GetType().Name);
                    try
                    {
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
                    // Find the first callback query handler that can process this callback update.
                    _logger.LogDebug("Searching for ITelegramCallbackQueryHandler for CBQ Data: '{CBQData}'. Available handlers: {HandlerCount}",
                        update.CallbackQuery.Data, _callbackQueryHandlers.Count()); // Log available handlers

                    foreach (var h_instance in _callbackQueryHandlers) // Add logging for each check
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
                    // Original line: callbackHandler = _callbackQueryHandlers.FirstOrDefault(h => h.CanHandle(update));
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
        /// آپدیت‌هایی را که توسط هیچ Command Handler یا وضعیت فعالی مدیریت نشده‌اند، مدیریت می‌کند.
        /// معمولاً با ارسال یک پیام پیش‌فرض به کاربر همراه است.
        /// </summary>
        /// <param name="update">آپدیت نامشخص یا بدون تطابق.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        private async Task HandleUnknownOrUnmatchedUpdateAsync(Update update, CancellationToken cancellationToken)
        {
            var messageText = update.Message?.Text ?? update.CallbackQuery?.Data ?? update.Type.ToString();
            var partialMessageText = messageText.Length > 50 ? messageText.Substring(0, 50) + "..." : messageText;
            _logger.LogWarning(
                "Handling unmatched update ID: {UpdateId}. UpdateType: {UpdateType}, Content (partial): '{PartialMessageText}'",
                update.Id,
                update.Type,
                partialMessageText);

            var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;
            if (chatId.HasValue)
            {
                // ارسال یک پیام پیش‌فرض به کاربر برای اطلاع از عدم درک درخواست.
                await _messageSender.SendTextMessageAsync(chatId.Value,
                    "Sorry, I didn't understand that. Please type /help to see available commands or check the menu.",
                    cancellationToken: cancellationToken);
            }
            else
            {
                _logger.LogWarning("Cannot send 'unknown command' message for update ID: {UpdateId} as ChatId is missing.", update.Id);
            }
        }

        /// <summary>
        /// خطاهای پیش‌بینی نشده در حین پردازش آپدیت را مدیریت می‌کند و یک پیام عمومی خطا به کاربر ارسال می‌کند.
        /// </summary>
        /// <param name="update">آپدیتی که در حین پردازش آن خطا رخ داده است.</param>
        /// <param name="exception">خطای رخ داده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات ارسال پیام (معمولاً CancellationToken.None استفاده می‌شود تا پیام خطا حتما ارسال شود).</param>
        private async Task HandleProcessingErrorAsync(Update update, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "Handling processing error for update ID: {UpdateId}. Attempting to notify user.", update.Id);
            var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;
            if (chatId.HasValue)
            {
                try
                {
                    // ارسال پیام خطای عمومی به کاربر.
                    // از CancellationToken.None استفاده می‌شود تا اطمینان حاصل شود که این پیام خطا حتی اگر درخواست اصلی (و CancellationToken آن)
                    // لغو شده باشد، همچنان شانس ارسال داشته باشد. این مهم است زیرا کاربر باید از وقوع مشکل مطلع شود.
                    await _messageSender.SendTextMessageAsync(chatId.Value,
                        "🤖 Oops! Something went wrong while processing your request. Our team has been notified. Please try again in a moment.",
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception sendEx)
                {
                    // اگر حتی ارسال پیام خطا نیز با مشکل مواجه شود، این یک خطای بحرانی‌تر است.
                    _logger.LogError(sendEx, "Critical: Failed to send error notification message to user {ChatId} for update ID: {UpdateId} after a processing error.", chatId.Value, update.Id);
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