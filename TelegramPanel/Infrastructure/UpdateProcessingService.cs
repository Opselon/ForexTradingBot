using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Polly; // اضافه شده برای Polly
using Polly.Retry; // اضافه شده برای سیاست‌های Retry
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums; // Required for UpdateType
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.Pipeline;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions; // برای TelegramPipelineDelegate

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
        IMemoryCache memoryCache,
        IEnumerable<ITelegramCallbackQueryHandler> callbackHandlers) // Add callbackHandlers to constructor
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _middlewares = middlewares?.Reverse().ToList().AsReadOnly() ?? throw new ArgumentNullException(nameof(middlewares));
            _commandHandlers = commandHandlers ?? throw new ArgumentNullException(nameof(commandHandlers));
            _callbackQueryHandlers = callbackQueryHandlers ?? throw new ArgumentNullException(nameof(callbackQueryHandlers));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _directMessageSender = directMessageSender ?? throw new ArgumentNullException(nameof(directMessageSender));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
       

        // تعریف _internalServiceRetryPolicy برای عملیات‌های داخلی (مانند دسترسی به DB از طریق StateMachine)
        _internalServiceRetryPolicy = Policy
                .Handle<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
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
                .Handle<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
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

            // This defines the final destination of the pipeline, which is our main router.
            async Task finalHandlerAction(Update processedUpdate, CancellationToken ct)
            {
                await RouteToHandlerOrStateMachineAsync(processedUpdate, ct);
            }

            // Build the middleware pipeline chain.
            var pipeline = _middlewares.Aggregate(
                (TelegramPipelineDelegate)finalHandlerAction,
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

                // --- THIS IS THE FIX ---
                // The call now matches the corrected 2-argument signature of HandleProcessingErrorAsync.
                await HandleProcessingErrorAsync(update, ex).ConfigureAwait(false);
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
        /// <summary>
        /// Main router method. Orchestrates the entire routing workflow with a top-level resiliency boundary.
        /// </summary>
        private async Task RouteToHandlerOrStateMachineAsync(Update update, CancellationToken cancellationToken)
        {
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            if (!userId.HasValue)
            {
                _logger.LogWarning("Cannot route Update ID: {UpdateId}. UserID is missing.", update.Id);
                return;
            }

            try
            {
                var pollyContext = CreatePollyContextForUpdate(update, userId.Value);

                await _internalServiceRetryPolicy.ExecuteAsync(
                    async (context, ct) => await ProcessUpdateInternalAsync(update, userId.Value, context, ct),
                    pollyContext,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical, non-recoverable error occurred while routing Update ID {UpdateId} for UserID {UserId} after all retries.", update.Id, userId.Value);
                await HandleProcessingErrorAsync(update, ex).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The core processing workflow, executed within a Polly policy.
        /// This version handles handler selection directly without a helper method.
        /// Priority Order: 1. Specific Handlers -> 2. State Machine -> 3. Fallback.
        /// </summary>
        private async Task ProcessUpdateInternalAsync(Update update, long userId, Polly.Context pollyContext, CancellationToken cancellationToken)
        {
            // --- Priority 1: Check for a specific Command or Callback Handler ---
            // The handler selection logic is now inlined here.
            bool handled = false;

            if (update.Type == UpdateType.Message && update.Message?.Text?.StartsWith('/') == true)
            {
                var commandHandler = _commandHandlers.FirstOrDefault(h => h.CanHandle(update));
                if (commandHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} to CommandHandler: {HandlerName}", update.Id, commandHandler.GetType().Name);
                    await commandHandler.HandleAsync(update, cancellationToken).ConfigureAwait(false);
                    handled = true;
                }
            }
            else if (update.Type == UpdateType.CallbackQuery)
            {
                var callbackHandler = _callbackQueryHandlers.FirstOrDefault(h => h.CanHandle(update));
                if (callbackHandler != null)
                {
                    _logger.LogInformation("Routing UpdateID {UpdateId} to CallbackQueryHandler: {HandlerName}", update.Id, callbackHandler.GetType().Name);
                    await callbackHandler.HandleAsync(update, cancellationToken).ConfigureAwait(false);
                    handled = true;
                }
            }

            // If a specific handler was found and executed, our work is done.
            if (handled)
            {
                return;
            }

            // --- Priority 2: Attempt to handle with the state machine ---
            if (await TryHandleWithStateMachineAsync(update, userId, pollyContext, cancellationToken).ConfigureAwait(false))
            {
                return; // Handled by the state machine path.
            }

            // --- Priority 3: Fallback for any unmatched updates ---
            await HandleUnknownOrUnmatchedUpdateAsync(update, cancellationToken).ConfigureAwait(false);
        }


        // These helper methods below can remain exactly as they were in the previous refactored answer.
        // They are not dependent on `FindSpecificHandlerFor` and are still highly valuable.

        /// <summary>
        /// Attempts to process the update using the state machine. Handles its own errors.
        /// </summary>
        /// <returns>True if the state machine path was taken, false if there was no active state.</returns>
        private async Task<bool> TryHandleWithStateMachineAsync(Update update, long userId, Polly.Context pollyContext, CancellationToken cancellationToken)
        {
            ITelegramState? currentState = await _stateMachine.GetCurrentStateAsync(userId, cancellationToken).ConfigureAwait(false);

            if (currentState == null)
            {
                return false; // No active state.
            }

            _logger.LogInformation("UserID {UserId} is in state '{StateName}'. Processing UpdateID {UpdateId} with state machine.", userId, currentState.Name, update.Id);

            try
            {
                await _stateMachine.ProcessUpdateInCurrentStateAsync(userId, update, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HandleStateMachineRecoveryAsync(update, userId, currentState.Name, ex).ConfigureAwait(false);
            }

            return true; // Path handled.
        }

        /// <summary>
        /// Handles recovery after a state machine execution failure (notifies user, clears state).
        /// </summary>
        private async Task HandleStateMachineRecoveryAsync(Update update, long userId, string failedStateName, Exception originalException)
        {
            _logger.LogError(originalException, "Error processing UpdateID {UpdateId} in state '{StateName}' for UserID {UserId}.", update.Id, failedStateName, userId);

            await HandleProcessingErrorAsync(update, originalException).ConfigureAwait(false);

            _logger.LogWarning("Attempting to clear faulty state '{StateName}' for UserID {UserId} as part of recovery.", failedStateName, userId);
            try
            {
                var recoveryContext = new Polly.Context($"StateClearRecovery_{update.Id}", new Dictionary<string, object> { { "UserId", userId } });

                await _internalServiceRetryPolicy.ExecuteAsync(
                    async (context, ct) => await _stateMachine.ClearStateAsync(userId, ct),
                    recoveryContext,
                    CancellationToken.None
                ).ConfigureAwait(false);

                _logger.LogInformation("State '{StateName}' cleared successfully for UserID {UserId} after processing error.", failedStateName, userId);
            }
            catch (Exception clearEx)
            {
                _logger.LogCritical(clearEx, "CRITICAL: Failed to clear state for UserID {UserId} after a processing error in state '{StateName}'.", userId, failedStateName);
            }
        }

        /// <summary>
        /// Creates a new Polly context for an operation.
        /// </summary>
        private Polly.Context CreatePollyContextForUpdate(Update update, long userId, string operationName = "UpdateProcessing")
        {
            return new Polly.Context($"{operationName}_{update.Id}", new Dictionary<string, object>
    {
        { "UpdateId", update.Id },
        { "TelegramUserId", userId }
    });
        }
        /// <summary>
        /// Handles updates that were not managed by any other handler. It safely
        /// launches a background "fire-and-forget" task to notify the user.
        /// </summary>
        private Task HandleUnknownOrUnmatchedUpdateAsync(Update update, CancellationToken cancellationToken)
        {
            try
            {
                var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;

                if (chatId.HasValue)
                {
                    // 1. ROBUST LAUNCH: Launch the self-contained, resilient background task.
                    _ = RespondToUnknownUpdateAndForgetAsync(chatId.Value, update.Id, cancellationToken);
                }
                else
                {
                    // 2. ENHANCED LOGGING: Include the UpdateType for better context.
                    _logger.LogWarning(
                        "Cannot handle unknown update {UpdateId} because ChatId is missing. UpdateType: {UpdateType}",
                        update.Id,
                        update.Type);
                }
            }
            catch (Exception ex)
            {
                // 3. LAUNCHER SAFETY NET: Prevents the main pipeline from crashing due to an error here.
                _logger.LogError(ex, "An unexpected error occurred while trying to launch the 'unknown update' handler for Update ID {UpdateId}.", update.Id);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// A self-contained, resilient background task to send an ephemeral "unknown command" message.
        /// It handles its own rate-limiting, error handling, and applies the external API retry policy.
        /// </summary>
        private async Task RespondToUnknownUpdateAndForgetAsync(long chatId, int updateId, CancellationToken cancellationToken)
        {
            // This entire method runs in the background. It MUST handle all of its own exceptions.
            try
            {
                var rateLimitCacheKey = $"unknown_command_ratelimit_{chatId}";
                if (_memoryCache.TryGetValue(rateLimitCacheKey, out _))
                {
                    _logger.LogInformation("Rate limit triggered for ChatId {ChatId}. Suppressing 'unknown command' message.", chatId);
                    return;
                }

                // 4. CORRECT RETRY POLICY APPLICATION
                var pollyContext = new Polly.Context($"EphemeralMessage_{updateId}", new Dictionary<string, object> { { "ChatId", chatId } });

                // Wrap the send operation with the retry policy.
                var sentMessage = await _externalApiRetryPolicy.ExecuteAsync(
                    async (context, ct) => await _directMessageSender.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Sorry, I didn't understand that command. This message will self-destruct.",
                        cancellationToken: ct
                    ),
                    pollyContext,
                    cancellationToken
                ).ConfigureAwait(false);

                if (sentMessage is null)
                {
                    _logger.LogWarning("Failed to send ephemeral message to chat {ChatId} after retries, it might be blocked or does not exist.", chatId);
                    return;
                }

                // If sending was successful, set the rate limit and plan the deletion.
                _memoryCache.Set(rateLimitCacheKey, true, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(10)));
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

                // Wrap the delete operation with the retry policy as well.
                await _externalApiRetryPolicy.ExecuteAsync(
                    async (context, ct) => await _directMessageSender.DeleteMessageAsync(
                        chatId: chatId,
                        messageId: sentMessage.MessageId,
                        cancellationToken: ct
                    ),
                    pollyContext,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // It's safe to ignore cancellation exceptions in a fire-and-forget task.
            }
            catch (Exception ex)
            {
                // Catch all other exceptions to prevent the background task from crashing the application.
                _logger.LogError(ex, "An unhandled exception occurred in the 'RespondToUnknownUpdateAndForgetAsync' background task for ChatId {ChatId}.", chatId);
            }
        }

        /// <summary>
        /// Manages unexpected exceptions during update processing by logging the original error
        /// and attempting to send a resilient "Oops" message to the user.
        /// </summary>
        /// <param name="update">The update that caused the processing error.</param>
        /// <param name="exception">The original exception that was thrown.</param>
        private async Task HandleProcessingErrorAsync(Update update, Exception exception)
        {
            // 1. ENHANCED LOGGING of the initial failure with more detail.
            _logger.LogError(exception,
                "Handling processing error for UpdateID {UpdateId}. Exception Type: {ExceptionType}. Error: {ErrorMessage}. Attempting to notify user.",
                update.Id,
                exception.GetType().Name,
                exception.Message);

            // 2. MORE COMPREHENSIVE ChatId/UserId retrieval.
            var chatId = update.Message?.Chat?.Id
                      ?? update.CallbackQuery?.Message?.Chat?.Id
                      ?? update.InlineQuery?.From?.Id
                      ?? update.ChosenInlineResult?.From?.Id
                      ?? update.MyChatMember?.Chat?.Id;

            if (chatId.HasValue)
            {
                // Create a dedicated Polly Context for this specific error-handling operation.
                var pollyContext = new Polly.Context($"ProcessingErrorMessage_{update.Id}", new Dictionary<string, object>
        {
            { "UpdateId", update.Id },
            { "ChatId", chatId.Value }
        });

                try
                {
                    // Use _externalApiRetryPolicy to make the user notification resilient.
                    // Using CancellationToken.None is a deliberate choice here: we want this emergency
                    // message to have the best possible chance of being delivered, even if the original
                    // operation was canceled.
                    await _externalApiRetryPolicy.ExecuteAsync(
                        async (context, ct) => await _messageSender.SendTextMessageAsync(
                            chatId.Value,
                            "🤖 Oops! Something went wrong while processing your request. Our team has been notified. Please try again in a moment.",
                            cancellationToken: ct // Pass Polly's cancellation token down.
                        ),
                        pollyContext,
                        CancellationToken.None // We explicitly tell Polly to start with a non-canceled token.
                    ).ConfigureAwait(false);
                }
                catch (Exception sendEx)
                {
                    // 3. MORE SPECIFIC LOGGING for the failure of the error notification system itself.
                    _logger.LogCritical(sendEx,
                        "CRITICAL: Failed to send the final error notification message to ChatId {ChatId} for UpdateID {UpdateId} after a processing error. Original Exception Type was {OriginalExceptionType}.",
                        chatId.Value,
                        update.Id,
                        exception.GetType().Name); // Include original exception type for context.
                }
            }
            else
            {
                // 4. MORE INFORMATIVE LOGGING when ChatId is missing.
                _logger.LogWarning(
                    "Could not notify user about processing error for UpdateID {UpdateId} because ChatId could not be determined. Original Exception Type: {ExceptionType}, Original Error: {OriginalErrorMessage}",
                    update.Id,
                    exception.GetType().Name,
                    exception.Message);
            }
        }
            #endregion
        }
}