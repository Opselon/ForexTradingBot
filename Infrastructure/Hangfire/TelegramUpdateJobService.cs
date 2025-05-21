// File: Infrastructure/Hangfire/TelegramUpdateJobService.cs
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces; // برای ITelegramUpdateProcessor

namespace Infrastructure.Hangfire
{
    public interface ITelegramUpdateJobService
    {
        Task ProcessTelegramUpdateAsync(Update update, CancellationToken cancellationToken = default);
    }

    public class TelegramUpdateJobService : ITelegramUpdateJobService
    {
        private readonly ITelegramUpdateProcessor _updateProcessor; // این همان UpdateProcessingService شماست
        private readonly ILogger<TelegramUpdateJobService> _logger;

        public TelegramUpdateJobService(
            ITelegramUpdateProcessor updateProcessor,
            ILogger<TelegramUpdateJobService> logger)
        {
            _updateProcessor = updateProcessor;
            _logger = logger;
        }

        public async Task ProcessTelegramUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            using (_logger.BeginScope(new Dictionary<string, object?> { ["HangfireJobType"] = "TelegramUpdateProcessing", ["UpdateId"] = update.Id }))
            {
                _logger.LogInformation("Hangfire Job: Starting processing of Telegram Update ID: {UpdateId}, Type: {UpdateType}", update.Id, update.Type);
                try
                {
                    await _updateProcessor.ProcessUpdateAsync(update, cancellationToken);
                    _logger.LogInformation("Hangfire Job: Successfully processed Telegram Update ID: {UpdateId}", update.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hangfire Job: Unhandled exception during processing Telegram Update ID: {UpdateId}", update.Id);
                    throw; // اجازه دهید Hangfire خطا را مدیریت کند
                }
            }
        }
    }
}