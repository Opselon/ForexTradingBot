// File: TelegramPanel/Application/CommandHandlers/Entry/StartCommandHandler.cs

using Application.DTOs;              // For RegisterUserDto and UserDto
using Application.Interfaces;        // For IUserService
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu; // For centralized menu
using TelegramPanel.Application.Interfaces; // For ITelegramCommandHandler
using TelegramPanel.Formatters;         // For TelegramMessageFormatter
using TelegramPanel.Infrastructure;

namespace TelegramPanel.Application.CommandHandlers.Entry
{
    public class StartCommandHandler : ITelegramCommandHandler, ITelegramCallbackQueryHandler
    {
        private readonly ILogger<StartCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine? _stateMachine;
        private readonly ITelegramBotClient _botClient;
        private readonly IServiceScopeFactory _scopeFactory;
        private const string ShowMainMenuCallback = "show_main_menu";

        public StartCommandHandler(
            ILogger<StartCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IServiceScopeFactory scopeFactory,
            ITelegramBotClient botClient,
            ITelegramStateMachine? stateMachine = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _stateMachine = stateMachine;
        }

        // --- CanHandle, HandleAsync, HandleShowMainMenuCallback, GenerateWelcomeMessageBody methods are all correct and do not need changes. ---
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                update.Message?.Text?.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase) == true
                ? true
                : update.Type == UpdateType.CallbackQuery &&
                update.CallbackQuery?.Data == ShowMainMenuCallback;
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

            _logger.LogInformation("Handling '{Callback}' for UserID: {UserId}, editing message {MessageId} to become main menu.",
                ShowMainMenuCallback, user.Id, messageId);

            try
            {
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);

                var effectiveUsername = !string.IsNullOrWhiteSpace(user.Username) ? user.Username : user.FirstName;
                var welcomeText = $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(effectiveUsername)}!*";
                var messageBody = GenerateWelcomeMessageBody(welcomeText, isExistingUser: true);

                // Uses the corrected helper method below
                var keyboard = GetMainMenuKeyboard();

                _ = await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: messageBody,
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken
                );

                if (_stateMachine != null)
                {
                    await _stateMachine.ClearStateAsync(user.Id, cancellationToken);
                }
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
            {
                _logger.LogInformation("Message {MessageId} was already the main menu. No action taken.", messageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling '{Callback}' for UserID {UserId}", ShowMainMenuCallback, user.Id);
            }
        }

        private string GenerateWelcomeMessageBody(string welcomeHeader, bool isExistingUser)
        {
            return $"{welcomeHeader}\n\n" +
                   "Your trusted companion for trading signals and market analysis.\n\n" +
                   "📊 *Available Features:*\n" +
                   "• 📈 Real-time alerts & signals\n" +
                   "• 💎 Professional trading tools\n" +
                   "• 📰 In-depth news analysis\n" +
                   (isExistingUser ? "• 💼 Portfolio tracking\n• 🔔 Customizable notifications\n\n" :
                    "• 💼 Portfolio tracking\n\n") +
                   "Use the menu below or type /help for more information.";
        }

        // --- The rest of the methods also use GetMainMenuKeyboard, so no further changes are needed in them. ---
        private async Task HandleStartCommand(Message message, CancellationToken cancellationToken)
        {
            var user = message.From!;
            var chatId = message.Chat.Id;

            _logger.LogInformation("FAST /start received for UserID: {UserId}. Sending initial generic welcome.", user.Id);

            Message? sentMessage;
            try
            {
                sentMessage = await SendInitialWelcomeMessageAsync(chatId, user.FirstName, cancellationToken);
                if (sentMessage == null)
                {
                    _logger.LogError("Failed to send initial welcome message and get its ID for UserID {UserId}.", user.Id);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not send initial /start welcome message to UserID {UserId}. Aborting.", user.Id);
                return;
            }

            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<StartCommandHandler>>();
                var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                var stateMachine = scope.ServiceProvider.GetService<ITelegramStateMachine>();
                var messageSender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();

                try
                {
                    var telegramUserId = user.Id.ToString();
                    var existingUser = await userService.GetUserByTelegramIdAsync(telegramUserId, cancellationToken);

                    bool isNewRegistration = existingUser == null;
                    string finalUsername;

                    if (isNewRegistration)
                    {
                        var firstName = user.FirstName ?? "";
                        var lastName = user.LastName ?? "";
                        var username = user.Username;
                        string effectiveUsername = !string.IsNullOrWhiteSpace(username) ? username : $"{firstName} {lastName}".Trim();
                        if (string.IsNullOrWhiteSpace(effectiveUsername))
                        {
                            effectiveUsername = $"User_{telegramUserId}";
                        }

                        _ = await userService.RegisterUserAsync(new RegisterUserDto { Username = effectiveUsername, TelegramId = telegramUserId, Email = $"{telegramUserId}@telegram.temp.user" }, cancellationToken);
                        finalUsername = effectiveUsername;
                        scopedLogger.LogInformation("[BackgroundScope] New user {finalUsername} registered successfully.", finalUsername);
                    }
                    else
                    {
                        finalUsername = existingUser!.Username;
                        scopedLogger.LogInformation("[BackgroundScope] Existing user {finalUsername} found.", finalUsername);
                        if (stateMachine != null)
                        {
                            await stateMachine.ClearStateAsync(user.Id, cancellationToken);
                        }
                    }

                    await EditWelcomeMessageWithDetailsAsync(
                        chatId,
                        sentMessage.MessageId,
                        finalUsername,
                        !isNewRegistration,
                        messageSender,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    scopedLogger.LogError(ex, "[BackgroundScope] Error during user processing or message edit for TelegramID {UserId}.", user.Id);
                }
            }, cancellationToken);
        }

        private Task<Message> SendInitialWelcomeMessageAsync(long chatId, string firstName, CancellationToken cancellationToken)
        {
            var welcomeText = $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(firstName)}! 👋\n\n" +
                              "🌟 *Welcome to the Forex Trading Bot*\n\n" +
                              "Initializing your profile, please wait...";

            var keyboard = GetMainMenuKeyboard();

            return _botClient.SendMessage(
                 chatId: chatId,
                 text: welcomeText,
                 parseMode: ParseMode.Markdown,
                 replyMarkup: keyboard,
                 cancellationToken: cancellationToken);
        }

        private Task EditWelcomeMessageWithDetailsAsync(long chatId, int messageId, string username, bool isExistingUser, ITelegramMessageSender messageSender, CancellationToken cancellationToken)
        {
            var welcomeText = isExistingUser ? $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(username)}!*" :
                                               $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(username)}! 👋\n\n🌟 *Welcome to the Forex Trading Bot*";

            var messageBody = GenerateWelcomeMessageBody(welcomeText, isExistingUser);

            var keyboard = GetMainMenuKeyboard();

            return messageSender.EditMessageTextAsync(
                chatId,
                messageId,
                messageBody,
                ParseMode.Markdown,
                keyboard,
                cancellationToken);
        }

        // --- THE ONLY CHANGE IS HERE ---
        /// <summary>
        /// Gets the main menu keyboard from the single, centralized source.
        /// </summary>
        /// <returns>An InlineKeyboardMarkup for the main menu.</returns>
        private InlineKeyboardMarkup GetMainMenuKeyboard()
        {
            // Now calls the static method from MenuCommandHandler to get the keyboard.
            // This ensures any changes to the main menu are reflected here automatically.
            return MenuCommandHandler.GetMainMenuMarkup().keyboard;
        }
    }
}