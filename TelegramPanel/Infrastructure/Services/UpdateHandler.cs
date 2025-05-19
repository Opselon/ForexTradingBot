using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using Telegram.Bot.Exceptions;

namespace TelegramPanel.Infrastructure.Services
{
    public class UpdateHandler : Telegram.Bot.Polling.IUpdateHandler
    {
        private readonly ILogger<UpdateHandler> _logger;
        private readonly ITelegramCallbackQueryHandler _callbackQueryHandler;

        public UpdateHandler(
            ILogger<UpdateHandler> logger,
            ITelegramCallbackQueryHandler callbackQueryHandler)
        {
            _logger = logger;
            _callbackQueryHandler = callbackQueryHandler;
        }

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.CallbackQuery != null)
                {
                    await _callbackQueryHandler.HandleAsync(update, cancellationToken);
                }
                else if (update.Message != null)
                {
                    _logger.LogInformation("Received message: {MessageType} from chat {ChatId}", update.Message.Type, update.Message.Chat.Id);
                }
                else
                {
                    _logger.LogWarning("Received unsupported update type: {UpdateType}", update.Type);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
            }
        }

        public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error (Source: {source}):\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => $"Polling Error (Source: {source}): {exception}"
            };

            _logger.LogError(exception, errorMessage);
            return Task.CompletedTask;
        }
    }
} 