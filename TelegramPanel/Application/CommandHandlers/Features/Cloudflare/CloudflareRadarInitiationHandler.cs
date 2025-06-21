// File: TelegramPanel/Application/CommandHandlers/Features/Cloudflare/CloudflareRadarInitiationHandler.cs
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces; // For ITelegramMessageSender
using Telegram.Bot; // Potentially needed implicitly by Telegram.Bot.Types
using Telegram.Bot.Exceptions; // For ApiRequestException if needed for specific error handling

namespace TelegramPanel.Application.CommandHandlers.Features.Cloudflare
{
    /// <summary>
    /// Handles the initial click on the "Cloudflare Radar" button from the main menu.
    /// Its sole responsibility is to transition the user to the first state of the Cloudflare Radar feature
    /// by invoking the appropriate display method on the main handler.
    /// </summary>
    public class CloudflareRadarInitiationHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<CloudflareRadarInitiationHandler> _logger;
        // Injected dependency on the handler that manages the state transitions and UI display.
        private readonly CloudflareRadarCallbackHandler _radarCallbackHandler;

        /// <summary>
        /// Initializes a new instance of the <see cref="CloudflareRadarInitiationHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance for this handler.</param>
        /// <param name="radarCallbackHandler">The main handler responsible for the Cloudflare Radar feature's UI and state management.</param>
        public CloudflareRadarInitiationHandler(
            ILogger<CloudflareRadarInitiationHandler> logger,
            CloudflareRadarCallbackHandler radarCallbackHandler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _radarCallbackHandler = radarCallbackHandler ?? throw new ArgumentNullException(nameof(radarCallbackHandler));
        }

        /// <summary>
        /// Determines if this handler can process the incoming Telegram update.
        /// It specifically looks for CallbackQuery updates with the "menu_cf_radar" data.
        /// </summary>
        /// <param name="update">The incoming Telegram update.</param>
        /// <returns>True if the update is a CallbackQuery with the correct data, false otherwise.</returns>
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data == MenuCommandHandler.CloudflareRadarCallbackData;
        }

        /// <summary>
        /// Handles the callback query originating from the "Cloudflare Radar" button.
        /// It answers the callback query to remove the loading state from the button, logs the initiation,
        /// and then delegates the task of showing the initial country selection menu to the main handler.
        /// </summary>
        /// <param name="update">The Telegram Update containing the CallbackQuery.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // Safely extract necessary information from the callback query.
            // `CanHandle` ensures these are not null here.
            var callbackQuery = update.CallbackQuery!;
            var userId = callbackQuery.From.Id;
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            _logger.LogInformation("Initiating Cloudflare Radar flow for User {UserId} in Chat {ChatId}", userId, chatId);

            try
            {
                // --- Answer the Callback Query ---
                // This is crucial for user feedback: it removes the "loading" state from the button they pressed.
                // We access the public 'MessageSender' property of _radarCallbackHandler to get the ITelegramMessageSender instance.
                await _radarCallbackHandler.MessageSender.AnswerCallbackQueryAsync( // CORRECTED: Using the public property.
                    callbackQueryId: callbackQuery.Id,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // Log any errors encountered while answering the callback, but allow the process to continue.
                _logger.LogWarning(ex, "Failed to answer callback query {CallbackQueryId} during initiation for User {UserId}.", callbackQuery.Id, userId);
            }

            // --- Delegate to the Main Handler ---
            // Now, invoke the main handler to display the country selection menu.
            // This transitions the user into the feature's workflow.
            await _radarCallbackHandler.ShowCountrySelectionMenuAsync(
                chatId: chatId,
                messageId: messageId,
                page: 1, // Start at the first page of countries
                cancellationToken: cancellationToken);

            _logger.LogInformation("Delegated showing country selection menu to CloudflareRadarCallbackHandler for User {UserId}.", userId);
        }
    }
}