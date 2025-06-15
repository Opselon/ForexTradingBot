using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Telegram.Bot.Types;

namespace TelegramPanel.Application.Pipeline
{
    public class LoggingMiddleware : ITelegramMiddleware
    {
        private readonly ILogger<LoggingMiddleware> _logger;

        public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
        {
            _logger = logger;
        }

        public async Task InvokeAsync(Update update, TelegramPipelineDelegate next, CancellationToken cancellationToken = default)
        {
            int updateId = update.Id;
            Telegram.Bot.Types.Enums.UpdateType updateType = update.Type;
            long? userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            Stopwatch stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "Processing Telegram Update: ID={UpdateId}, Type={UpdateType}, UserID={UserId}",
                updateId, updateType, userId?.ToString() ?? "N/A");

            try
            {
                await next(update, cancellationToken);
                stopwatch.Stop();
                _logger.LogInformation(
                    "Finished processing Telegram Update: ID={UpdateId}, Duration={DurationMs}ms",
                    updateId, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex,
                    "Error processing Telegram Update: ID={UpdateId}, Type={UpdateType}, UserID={UserId}, Duration={DurationMs}ms. Error: {ErrorMessage}",
                    updateId, updateType, userId?.ToString() ?? "N/A", stopwatch.ElapsedMilliseconds, ex.Message);
                // می‌توانید اینجا یک پیام خطا به کاربر یا ادمین ارسال کنید
                // throw; // یا خطا را دوباره throw کنید تا توسط یک لایه بالاتر مدیریت شود
            }
        }
    }
}