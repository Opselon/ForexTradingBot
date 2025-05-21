using Application.Common.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    public class TelegramUserApiInitializationService : BackgroundService
    {
        private readonly ILogger<TelegramUserApiInitializationService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;

        public TelegramUserApiInitializationService(
            ILogger<TelegramUserApiInitializationService> logger,
            ITelegramUserApiClient userApiClient)
        {
            _logger = logger;
            _userApiClient = userApiClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Initializing Telegram User API client...");
                await _userApiClient.ConnectAndLoginAsync(stoppingToken);
                _logger.LogInformation("Telegram User API client initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Telegram User API client.");
                throw; // Rethrow to ensure the application fails to start if we can't connect
            }
        }
    }
} 