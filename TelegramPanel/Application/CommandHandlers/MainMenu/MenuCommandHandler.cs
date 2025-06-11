// File: TelegramPanel/Application/CommandHandlers/MenuCommandHandler.cs
#region Usings
using Microsoft.Extensions.Logging;
using System;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;
#endregion

namespace TelegramPanel.Application.CommandHandlers.MainMenu
{
    public class MenuCommandHandler : ITelegramCommandHandler
    {
        #region Private Fields
        private readonly ILogger<MenuCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;

        // Callback Data constants for menu buttons

        public const string SignalsCallbackData = "menu_view_signals";
        public const string ProfileCallbackData = "menu_my_profile";
        public const string SubscribeCallbackData = "menu_subscribe_plans";
        public const string SettingsCallbackData = "menu_user_settings";
        public const string MarketAnalysisData = "market_analysis";
        public const string AnalysisCallbackData = "menu_analysis";
        public const string EconomicCalendarCallbackData = "menu_econ_calendar"; // <<< NEW
        public const string BackToMainMenuGeneral = "back_to_main_menu"; //  Add this

        #endregion

        #region Constructor
        public MenuCommandHandler(ILogger<MenuCommandHandler> logger, ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }
        #endregion

        #region Static Menu Markup Generation


        /// <summary>
        /// Generates the text and inline keyboard markup for the main application menu.
        /// </summary>
        public static (string text, InlineKeyboardMarkup keyboard) GetMainMenuMarkup()
        {
            var text = "Welcome to the Main Menu!\nChoose one of the available options:";

            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] // Row 1: Core Features
                {
                    InlineKeyboardButton.WithCallbackData("📈 View Signals", SignalsCallbackData),
                    InlineKeyboardButton.WithCallbackData("📊 Market Analysis", MarketAnalysisData)
                },
                new[] // Row 2: NEW Analysis Button
                {
                    InlineKeyboardButton.WithCallbackData("🔍 News Analysis", AnalysisCallbackData),
                    InlineKeyboardButton.WithCallbackData("🗓️ Economic Calendar", EconomicCalendarCallbackData)

                },
                new[] // Row 3: Subscription
                {
                    InlineKeyboardButton.WithCallbackData("💎 Subscribe / Plans", SubscribeCallbackData)
                },
                new[] // Row 4: Account Management
                {
                    InlineKeyboardButton.WithCallbackData("⚙️ Settings", SettingsCallbackData),
                    InlineKeyboardButton.WithCallbackData("👤 My Profile", ProfileCallbackData)
                }
            );
            return (text, keyboard);
        }

        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            // THIS IS THE CORRECTED LOGIC FOR A COMMAND HANDLER
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/menu", StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>
        /// Handles the request to view the main menu triggered by a message (e.g., /menu command).
        /// </summary>
        /// <param name="update">The update containing the message.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // This logic correctly handles the /menu command by sending the menu.
            var message = update.Message; // This is now guaranteed to be from a Message update based on CanHandle.

            // Basic null check, though CanHandle should prevent this.
            if (message == null)
            {
                // Log a warning as this indicates a potential issue in CanHandle logic or update structure.
                _logger.LogWarning("MenuCommand: Message is null in UpdateID {UpdateId}, despite CanHandle passing.", update.Id);
                return; // Exit early if message is unexpectedly null.
            }

            var chatId = message.Chat.Id;
            var userId = message.From?.Id; // For logging purposes

            _logger.LogInformation("Handling /menu command for ChatID {ChatId}, UserID {UserId}", chatId, userId);

            try
            {
                // Use the static GetMainMenuMarkup method to get the message content and keyboard.
                // Ensure GetMainMenuMarkup is implemented to return both text and inline keyboard.
                var (text, inlineKeyboard) = GetMainMenuMarkup();

                // Send the main menu message to the user.
                // This call is a potential point of failure (Telegram API communication).
                await _messageSender.SendTextMessageAsync(
                    chatId: chatId,
                    text: text, // Use the text from GetMainMenuMarkup
                    parseMode: ParseMode.MarkdownV2, // Assuming text might have Markdown. Ensure text is properly escaped for MarkdownV2 if needed.
                    replyMarkup: inlineKeyboard,
                    cancellationToken: cancellationToken);

                _logger.LogDebug("Main menu sent successfully to ChatID {ChatId} via /menu command.", chatId);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                // This might happen if the bot is shutting down.
                _logger.LogInformation(ex, "Sending main menu to ChatID {ChatId} was cancelled.", chatId);
                // No need to send an error message here as the operation was cancelled externally.
            }
            catch (Exception ex)
            {
                // Catch any other unexpected exceptions during the process (e.g., Telegram API errors).
                // Log the error details.
                _logger.LogError(ex, "An unexpected error occurred while sending the main menu to ChatID {ChatId}, UserID {UserId}", chatId, userId);

                // Optionally, attempt to send a fallback error message to the user.
                // This SendTextMessageAsync might also fail, so a nested try-catch or a robust SendMessage wrapper is advisable if this is critical.
                // try
                // {
                //     await _messageSender.SendTextMessageAsync(
                //         chatId: chatId,
                //         text: "An unexpected error occurred while trying to show the menu. Please try again later.",
                //         cancellationToken: cancellationToken);
                // }
                // catch (Exception sendErrorEx)
                // {
                //     // Log if sending the error message also fails.
                //     _logger.LogError(sendErrorEx, "Failed to send fallback error message to ChatID {ChatId} after menu command failure.", chatId);
                // }
            }
        }
        #endregion

    }
}