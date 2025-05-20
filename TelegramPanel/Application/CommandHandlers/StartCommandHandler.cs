using Application.DTOs;              // برای RegisterUserDto و UserDto از پروژه اصلی Application
using Application.Interfaces;        // برای IUserService از پروژه اصلی Application
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces; // برای ITelegramCommandHandler
using TelegramPanel.Formatters;         // برای TelegramMessageFormatter
using TelegramPanel.Infrastructure;       // برای ITelegramMessageSender

namespace TelegramPanel.Application.CommandHandlers
{
    public class StartCommandHandler : ITelegramCommandHandler, ITelegramCallbackQueryHandler
    {
        private readonly ILogger<StartCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IUserService _userService; // از پروژه Application اصلی
        private readonly ITelegramStateMachine? _stateMachine; // اختیاری، اگر نیاز به تغییر وضعیت دارید
        private readonly ITelegramBotClient _botClient; // Added ITelegramBotClient

        private const string ShowMainMenuCallback = "show_main_menu";

        public StartCommandHandler(
            ILogger<StartCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IUserService userService,
            ITelegramBotClient botClient, // Added ITelegramBotClient
            ITelegramStateMachine? stateMachine = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient)); // Added ITelegramBotClient
            _stateMachine = stateMachine;
        }

        public bool CanHandle(Update update)
        {
            if (update.Type == UpdateType.Message &&
                update.Message?.Text?.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
            if (update.Type == UpdateType.CallbackQuery &&
                update.CallbackQuery?.Data == ShowMainMenuCallback)
            {
                return true;
            }
            _logger.LogTrace("[HANDLER_NAME] CanHandle: NO for data: {CallbackData}. UpdateType: {UpdateType}", update.CallbackQuery?.Data, update.Type); // Add this log
            return false;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            if (update.Type == UpdateType.Message && update.Message?.From != null)
            {
                await HandleStartCommand(update.Message, cancellationToken);
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Message != null && update.CallbackQuery.From != null)
            {
                await HandleShowMainMenuCallback(update.CallbackQuery, cancellationToken);
            }
            else
            {
                _logger.LogWarning("StartCommandHandler: Invalid update type or missing data. UpdateID {UpdateId}.", update.Id);
            }
        }

        private async Task HandleShowMainMenuCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var message = callbackQuery.Message!; // Null check for Message should happen before calling this
            var user = callbackQuery.From;
            var chatId = message.Chat.Id;
            var messageId = message.MessageId; // The ID of the message with the "Back to Main Menu" button

            _logger.LogInformation("Handling '{Callback}' for UserID: {UserId}, ChatID: {ChatId}",
                ShowMainMenuCallback, user.Id, chatId);

            try
            {
                // 1. Answer the callback query immediately to remove the loading spinner on the client.
                //    No specific text is needed here if we're about to change the message content significantly.
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);

                // 2. Option A: Edit the current message to *become* the main menu.
                //    This is often a cleaner experience than sending a new message after closing the old one.
                //    It avoids chat clutter.

                // string mainMenuText = $"Welcome back to the Main Menu, {EscapeMarkdown(user.FirstName)}!";
                // InlineKeyboardMarkup mainMenuKeyboard = GetMainMenuKeyboard(); // You need a method to generate this

                // await _botClient.EditMessageTextAsync( // Or _messageSender.EditMessageTextAsync
                //     chatId: chatId,
                //     messageId: messageId,
                //     text: mainMenuText,
                //     parseMode: ParseMode.Markdown,
                //     replyMarkup: mainMenuKeyboard,
                //     cancellationToken: cancellationToken);
                // _logger.LogInformation("Edited message {MessageId} in ChatID {ChatId} to show main menu.", messageId, chatId);

                // 2. Option B: Delete the old message and send a new main menu message.
                //    This can also be clean if the old message is context-specific and no longer needed.
                //    However, deleting messages can sometimes fail or be delayed.
                //    Editing (Option A) is often more reliable if the message still makes sense to be the "host" for the main menu.

                // For now, let's stick to your original approach of editing to a "closing" message,
                // then sending a new main menu message, but we'll make the "closing" message optional or quicker.

                // Minimal "closing" message, or skip if sending new menu immediately
                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    "🔹 Returning to main menu...", // Shorter, less stateful message
                    ParseMode.Markdown,
                    null, // Remove keyboard from the old message
                    cancellationToken);
                _logger.LogDebug("Edited market analysis message {MessageId} to 'returning to menu'.", messageId);


                // 3. Send the main menu as a NEW message.
                // This is your existing SendMainMenuMessage call.
                var effectiveUsername = !string.IsNullOrWhiteSpace(user.Username) ? user.Username : $"{user.FirstName} {user.LastName}".Trim();
                if (string.IsNullOrWhiteSpace(effectiveUsername)) effectiveUsername = $"User_{user.Id}";

                // Ensure SendMainMenuMessage actually sends a message with the main menu keyboard.
                await SendMainMenuMessage(chatId, effectiveUsername, cancellationToken, isExistingUser: true); // isExistingUser might not be needed if context is just showing menu
                _logger.LogInformation("Sent main menu message to ChatID {ChatId}.", chatId);


                // 4. Clear user state if applicable (Good practice)
                if (_stateMachine != null) // Ensure _stateMachine is not null
                {
                    await _stateMachine.ClearStateAsync(user.Id, cancellationToken);
                    _logger.LogInformation("Cleared state for UserID: {UserId}", user.Id);
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
            {
                _logger.LogInformation("Message was not modified (already showing 'returning to menu' or similar). Error: {Error}", apiEx.Message);
                // If the edit to "returning to menu" failed because it was already that,
                // we should still try to send the main menu message if it hasn't been sent.
                // This block usually means the EditMessageTextAsync for the "closing" message failed.
                // The main menu sending should still proceed if not part of this try-catch.
                // To handle this better, the SendMainMenuMessage could be outside this specific catch,
                // or we ensure the logic proceeds.
                // For now, if this specific edit fails, the SendMainMenuMessage call above will still execute
                // unless an exception in AnswerCallbackQueryAsync bubbles up.

                // If we are here, it's likely the edit to "returning to main menu" failed.
                // We might still want to attempt sending the main menu if that's robust.
                // Consider if SendMainMenuMessage should be called even if this edit fails.
                // The current structure will proceed to SendMainMenuMessage.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling '{Callback}' for UserID {UserId}, ChatID {ChatId}.", ShowMainMenuCallback, user.Id, chatId);
                // Don't try to AnswerCallbackQueryAsync again if it was already answered at the start of the try block.
                // If the error happened before the initial AnswerCallbackQueryAsync, then attempting it here is fine.
                // The AnswerCallbackQueryAsync was moved to the top, so here we just log.
                // Optionally, send an error message to the user if the *main menu display itself* failed.
            }
        }

        private async Task HandleStartCommand(Message message, CancellationToken cancellationToken)
        {
            var telegramUserId = message.From!.Id.ToString();
            var firstName = message.From.FirstName ?? "";
            var lastName = message.From.LastName ?? "";
            var username = message.From.Username;
            var chatId = message.Chat.Id;

            string effectiveUsername = !string.IsNullOrWhiteSpace(username) ? username : $"{firstName} {lastName}".Trim();
            if (string.IsNullOrWhiteSpace(effectiveUsername)) effectiveUsername = $"User_{telegramUserId}";

            _logger.LogInformation("Handling /start command for TelegramUserID: {TelegramUserId}, ChatID: {ChatId}, EffectiveUsername: {EffectiveUsername}",
                telegramUserId, chatId, effectiveUsername);

            try
            {
                var existingUser = await _userService.GetUserByTelegramIdAsync(telegramUserId, cancellationToken);

                if (existingUser != null)
                {
                    _logger.LogInformation("Existing user {Username} (TelegramID: {TelegramId}) initiated /start.", existingUser.Username, telegramUserId);
                    await SendMainMenuMessage(chatId, existingUser.Username, cancellationToken, isExistingUser: true);
                    if (_stateMachine != null) await _stateMachine.ClearStateAsync(message.From.Id, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("New user initiating /start. TelegramID: {TelegramId}, EffectiveUsername: {EffectiveUsername}. Registering...",
                        telegramUserId, effectiveUsername);
                    string emailForRegistration = $"{telegramUserId}@telegram.temp.user";
                    var registerDto = new RegisterUserDto
                    {
                        Username = effectiveUsername,
                        TelegramId = telegramUserId,
                        Email = emailForRegistration
                    };
                    var newUser = await _userService.RegisterUserAsync(registerDto, cancellationToken);
                    _logger.LogInformation("User {Username} (ID: {UserId}, TelegramID: {TelegramId}) registered successfully with email {Email}.",
                        newUser.Username, newUser.Id, newUser.TelegramId, emailForRegistration);
                    await SendMainMenuMessage(chatId, newUser.Username, cancellationToken, isExistingUser: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling /start command for TelegramUserID {TelegramUserId}. EffectiveUsername: {EffectiveUsername}", telegramUserId, effectiveUsername);
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while processing your request. Please try again.", cancellationToken: cancellationToken);
            }
        }

        // Helper method to send the main menu message to avoid code duplication
        private async Task SendMainMenuMessage(long chatId, string username, CancellationToken cancellationToken, bool isExistingUser)
        {
            var welcomeText = isExistingUser ? $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(username)}!*" :
                                               $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(username)}! 👋\n\n🌟 *Welcome to Gold Market Trading Bot*";

            var messageBody = $"{welcomeText}\n\n" +
                              "Your trusted companion for gold trading signals and market analysis.\n\n" +
                              "📊 *Available Features:*\n" +
                              "• 📈 Real-time gold price alerts\n" +
                              "• 💎 Professional trading signals\n" +
                              "• 📰 Market analysis and insights\n" +
                              (isExistingUser ? "• 💼 Portfolio tracking\n• 🔔 Customizable notifications\n\n" :
                               "• 💼 Portfolio tracking\n\n") +
                              "Use the menu below or type /help for more information."; // Updated to reflect menu usage

            // --- UPDATED KEYBOARD for /start ---
            var keyboard = new InlineKeyboardMarkup(new[]
            {
            // Row 1
            new []
            {
                InlineKeyboardButton.WithCallbackData("📈 Gold Signals", MenuCommandHandler.SignalsCallbackData), // Existing
                InlineKeyboardButton.WithCallbackData("📊 Market Analysis", "market_analysis")                     // Existing (handled by MarketAnalysisCallbackHandler)
            },
            // Row 2
            new []
            {
                InlineKeyboardButton.WithCallbackData("💎 Subscribe", MenuCommandHandler.SubscribeCallbackData), // <<<< NEW/MODIFIED for subscribe
                InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData)     // Existing
            },
            // Row 3
            new []
            {
                InlineKeyboardButton.WithCallbackData("👤 My Profile", MenuCommandHandler.ProfileCallbackData) // Existing
                // You could add another button here if desired, e.g., Help
                // InlineKeyboardButton.WithCallbackData("❓ Help", "menu_help_info") // Example for a help button
            }
            // You can add more rows for other primary actions
        });

            await _messageSender.SendTextMessageAsync(
                chatId,
                messageBody,
                ParseMode.MarkdownV2,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }
}