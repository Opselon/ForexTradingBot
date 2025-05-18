using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.Pipeline; // برای TelegramPipelineDelegate

namespace TelegramPanel.Infrastructure // یا Application اگر در آن لایه است
{
    /// <summary>
    /// سرویس اصلی برای پردازش آپدیت‌های دریافتی تلگرام.
    /// این سرویس یک پایپ‌لاین از Middleware ها را اجرا کرده و سپس آپدیت را به
    /// ماشین وضعیت (<see cref="ITelegramStateMachine"/>) یا یک Command Handler مناسب (<see cref="ITelegramCommandHandler"/>) مسیریابی می‌کند.
    /// </summary>
    public class UpdateProcessingService : ITelegramUpdateProcessor
    {
        private readonly ILogger<UpdateProcessingService> _logger;
        private readonly IServiceProvider _serviceProvider; // برای resolve کردن سرویس‌های Scoped مانند ITelegramMessageSender در صورت نیاز مستقیم
        private readonly IReadOnlyList<ITelegramMiddleware> _middlewares; // Middleware های رجیستر شده، به ترتیب اجرا
        private readonly IEnumerable<ITelegramCommandHandler> _commandHandlers;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly ITelegramMessageSender _messageSender; // برای ارسال پیام‌های پیش‌فرض یا خطا

        public UpdateProcessingService(
            ILogger<UpdateProcessingService> logger,
            IServiceProvider serviceProvider, // تزریق IServiceProvider برای resolve کردن MessageSender در صورت نیاز در متدهای خطا
            IEnumerable<ITelegramMiddleware> middlewares,
            IEnumerable<ITelegramCommandHandler> commandHandlers,
            ITelegramStateMachine stateMachine,
            ITelegramMessageSender messageSender) // تزریق مستقیم MessageSender
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            // ترتیب Middleware ها: اگر می‌خواهید ترتیب رجیستر شدن در DI رعایت شود، نیازی به OrderBy یا Reverse نیست.
            // اگر ترتیب خاصی مد نظر است، می‌توان از یک Attribute یا یک قرارداد نام‌گذاری استفاده کرد.
            // فرض می‌کنیم ترتیب رجیستر شدن در DI صحیح است و اولین رجیستر شده، اولین اجرا شونده است.
            // پایپ‌لاین از انتها به ابتدا ساخته می‌شود، پس اولین Middleware در لیست، آخرین لایه قبل از Handler خواهد بود.
            // برای اینکه اولین Middleware رجیستر شده اول اجرا شود، لیست را Reverse می‌کنیم.
            _middlewares = middlewares?.Reverse().ToList().AsReadOnly() ?? throw new ArgumentNullException(nameof(middlewares));
            _commandHandlers = commandHandlers ?? throw new ArgumentNullException(nameof(commandHandlers));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }

        /// <summary>
        /// آپدیت تلگرام را با عبور از پایپ‌لاین Middleware ها پردازش کرده و به Handler مناسب ارسال می‌کند.
        /// </summary>
        public async Task ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            // از Log Scope استفاده شده در UpdateQueueConsumerService، پس اطلاعات آپدیت در لاگ‌ها خواهد بود.
            _logger.LogInformation("Beginning pipeline processing for update.");

            // تعریف نقطه پایانی پایپ‌لاین که آپدیت را به StateMachine یا CommandHandler می‌فرستد.
            TelegramPipelineDelegate finalHandlerAction = async (processedUpdate, ct) =>
            {
                await RouteToHandlerOrStateMachineAsync(processedUpdate, ct);
            };

            // ساخت پایپ‌لاین Middleware ها از انتها به ابتدا.
            // هر Middleware، delegate بعدی در زنجیره را به عنوان پارامتر `next` دریافت می‌کند.
            var pipeline = _middlewares.Aggregate(
                finalHandlerAction, // نقطه شروع (داخلی‌ترین عمل)
                (nextMiddlewareInChain, currentMiddleware) => // nextMiddlewareInChain نتیجه قبلی Aggregate است
                    async (upd, ct) => await currentMiddleware.InvokeAsync(upd, nextMiddlewareInChain, ct)
            );

            try
            {
                // اجرای کل پایپ‌لاین با آپدیت ورودی.
                await pipeline(update, cancellationToken);
                _logger.LogInformation("Pipeline processing completed for update.");
            }
            catch (Exception ex)
            {
                // این catch block برای خطاهایی است که توسط Middleware ها یا Handler ها catch نشده‌اند.
                _logger.LogError(ex, "An unhandled exception escaped the Telegram update processing pipeline.");
                // تلاش برای ارسال پیام خطای عمومی به کاربر.
                await HandleProcessingErrorAsync(update, ex, cancellationToken);
            }
        }

        /// <summary>
        /// آپدیت را به ماشین وضعیت (اگر کاربر در وضعیتی باشد) یا به یک Command Handler مناسب مسیریابی می‌کند.
        /// </summary>
        private async Task RouteToHandlerOrStateMachineAsync(Update update, CancellationToken cancellationToken)
        {
            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            if (!userId.HasValue)
            {
                _logger.LogWarning("Cannot route update: UserID is missing from the update object.");
                return; // بدون UserID، نمی‌توان وضعیت یا دستور خاص کاربر را پردازش کرد.
            }

            // اولویت ۱: بررسی و پردازش توسط ماشین وضعیت (State Machine)
            // اگر کاربر در یک مکالمه چند مرحله‌ای قرار دارد، آپدیت باید توسط وضعیت فعلی او پردازش شود.
            ITelegramState? currentState = null;
            try
            {
                currentState = await _stateMachine.GetCurrentStateAsync(userId.Value, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving current state for UserID {UserId}.", userId.Value);
                await HandleProcessingErrorAsync(update, ex, cancellationToken); // اطلاع به کاربر از خطا
                return;
            }

            if (currentState != null)
            {
                _logger.LogInformation("UserID {UserId} is in state '{StateName}'. Processing update with state machine.", userId.Value, currentState.Name);
                try
                {
                    await _stateMachine.ProcessUpdateInCurrentStateAsync(userId.Value, update, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing update in state '{StateName}' for UserID {UserId}.", currentState.Name, userId.Value);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken); // اطلاع به کاربر از خطا
                    await _stateMachine.ClearStateAsync(userId.Value, cancellationToken); // پاک کردن وضعیت در صورت خطا
                }
                return; // اگر وضعیت، آپدیت را مدیریت کرد، معمولاً به Command Handler ها نمی‌رویم.
            }

            // اولویت ۲: مسیریابی به Command Handler مناسب
            // اگر کاربر در هیچ وضعیت خاصی نیست، سعی می‌کنیم یک Command Handler برای آپدیت پیدا کنیم.
            // (مثلاً برای دستورات عمومی مانند /start, /help, /menu)
            ITelegramCommandHandler? handler = null;
            try
            {
                // Handler ها باید به ترتیبی که می‌خواهیم بررسی شوند، در IEnumerable باشند.
                // FirstOrDefault اولین Handler ای را که CanHandle برایش true باشد، برمی‌گرداند.
                handler = _commandHandlers.FirstOrDefault(h => h.CanHandle(update));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while trying to find a suitable command handler.");
                await HandleProcessingErrorAsync(update, ex, cancellationToken);
                return;
            }


            if (handler != null)
            {
                _logger.LogInformation("Routing update to command handler: {HandlerName}", handler.GetType().Name);
                try
                {
                    await handler.HandleAsync(update, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing command handler {HandlerName}.", handler.GetType().Name);
                    await HandleProcessingErrorAsync(update, ex, cancellationToken);
                }
            }
            else
            {
                // اگر هیچ Handler یا وضعیت فعالی آپدیت را مدیریت نکرد.
                await HandleUnknownOrUnmatchedUpdateAsync(update, cancellationToken);
            }
        }

        /// <summary>
        /// آپدیت‌هایی را که توسط هیچ Command Handler یا وضعیت فعالی مدیریت نشده‌اند، مدیریت می‌کند.
        /// </summary>
        private async Task HandleUnknownOrUnmatchedUpdateAsync(Update update, CancellationToken cancellationToken)
        {
            var messageText = update.Message?.Text ?? update.CallbackQuery?.Data ?? update.Type.ToString();
            _logger.LogWarning(
                "No suitable command handler or active state found for Update. UpdateType: {UpdateType}, Content (partial): '{MessageText}'",
                update.Type,
                messageText.Length > 50 ? messageText.Substring(0, 50) + "..." : messageText);

            var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;
            if (chatId.HasValue)
            {
                // ارسال یک پیام پیش‌فرض به کاربر
                await _messageSender.SendTextMessageAsync(chatId.Value,
                    "Sorry, I didn't understand that. Please type /help to see available commands or check the menu.",
                    cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// خطاهای پیش‌بینی نشده در حین پردازش آپدیت را مدیریت می‌کند و یک پیام به کاربر ارسال می‌کند.
        /// </summary>
        private async Task HandleProcessingErrorAsync(Update update, Exception exception, CancellationToken cancellationToken)
        {
            // (این متد را قبلاً در پاسخ به بخش اول سوال شما در UpdateProcessingService داشتیم)
            var chatId = update.Message?.Chat?.Id ?? update.CallbackQuery?.Message?.Chat?.Id;
            if (chatId.HasValue)
            {
                try
                {
                    await _messageSender.SendTextMessageAsync(chatId.Value,
                        "🤖 Oops! Something went wrong while processing your request. Our team has been notified. Please try again in a moment.",
                        cancellationToken: CancellationToken.None); // از CancellationToken.None استفاده کنید تا پیام خطا حتی اگر درخواست اصلی کنسل شده، ارسال شود.
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "Critical: Failed to send error notification message to user {ChatId} after a processing error.", chatId.Value);
                }
            }
        }
    }
}