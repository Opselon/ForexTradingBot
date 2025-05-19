using Application.DTOs;              // برای RegisterUserDto و UserDto از پروژه اصلی Application
using Application.Interfaces;        // برای IUserService از پروژه اصلی Application
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces; // برای ITelegramCommandHandler
using TelegramPanel.Formatters;         // برای TelegramMessageFormatter
using TelegramPanel.Infrastructure;       // برای ITelegramMessageSender
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramPanel.Application.CommandHandlers
{
    public class StartCommandHandler : ITelegramCommandHandler
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
            var message = callbackQuery.Message!;
            var user = callbackQuery.From;
            var chatId = message.Chat.Id;
            var messageId = message.MessageId;

            _logger.LogInformation("Handling {Callback} callback for TelegramUserID: {TelegramUserId}, ChatID: {ChatId}", ShowMainMenuCallback, user.Id, chatId);

            try
            {
                // 1. Answer the callback query to remove the loading spinner
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);

                // 2. Edit the previous message (e.g., the market analysis message)
                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    "🔹 Market analysis closed. Returning to main menu...",
                    ParseMode.Markdown, // Or ParseMode.Default if no formatting
                    null, // No keyboard
                    cancellationToken);

                // 3. Send the main menu (similar to /start for an existing user)
                // We need user's info, let's assume they are an existing user for simplicity here.
                // For a more robust solution, you might fetch user details again or pass them.
                var effectiveUsername = !string.IsNullOrWhiteSpace(user.Username) ? user.Username : $"{user.FirstName} {user.LastName}".Trim();
                if (string.IsNullOrWhiteSpace(effectiveUsername)) effectiveUsername = $"User_{user.Id}";

                await SendMainMenuMessage(chatId, effectiveUsername, cancellationToken, isExistingUser: true);

                if (_stateMachine != null)
                {
                    await _stateMachine.ClearStateAsync(user.Id, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling {Callback} callback for TelegramUserID {TelegramUserId}.", ShowMainMenuCallback, user.Id);
                // Optionally, send an error message to the user if the callback answer failed or text send failed.
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
                              "🌟 *Gold Market Trading Bot*\n\n" +
                              "Your trusted companion for gold trading signals and market analysis.\n\n" +
                              "📊 *Available Features:*\n" +
                              "• 📈 Real-time gold price alerts\n" +
                              "• 💎 Professional trading signals\n" +
                              "• 📰 Market analysis and insights\n" +
                              (isExistingUser ? "• 💼 Portfolio tracking\n• 🔔 Customizable notifications\n\n" :
                               "• 💼 Portfolio tracking\n\n") +
                              "Type /menu to see available options or /help for more information.";

            var keyboard = MenuCommandHandler.GetMainMenuKeyboard();

            await _messageSender.SendTextMessageAsync(
                chatId,
                messageBody,
                ParseMode.MarkdownV2,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }
}