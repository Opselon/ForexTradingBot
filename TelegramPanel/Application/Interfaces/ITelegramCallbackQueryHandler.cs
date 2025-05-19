using Telegram.Bot.Types;

namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// Interface for handling Telegram callback queries (button clicks)
    /// </summary>
    public interface ITelegramCallbackQueryHandler : ITelegramCommandHandler
    {
        // Inherits CanHandle and HandleAsync from ITelegramCommandHandler
        // Additional callback-specific methods can be added here if needed
    }
} 