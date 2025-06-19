// File: TelegramPanel/Application/CommandHandlers/Entry/StartCommandHandler.cs

#region Usings
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
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
// --- Domain Usings ---
using Domain.Entities; // For User, TokenWallet
using Domain.Enums;    // For UserLevel
// --- Repository Using ---
using Application.Common.Interfaces; // For IUserRepository
#endregion

namespace TelegramPanel.Application.CommandHandlers.Entry
{
    public class StartCommandHandler : ITelegramCommandHandler, ITelegramCallbackQueryHandler
    {
        private readonly ILogger<StartCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine? _stateMachine;
        private readonly ITelegramBotClient _botClient;
        private readonly IServiceScopeFactory _scopeFactory;

        // IUserRepository is injected to do a quick existence check before creating entities.
        private readonly IUserRepository _userRepository;
        private const string ShowMainMenuCallback = "show_main_menu";

        public StartCommandHandler(
            ILogger<StartCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IServiceScopeFactory scopeFactory,
            ITelegramBotClient botClient,
            ITelegramStateMachine? stateMachine = null,
            IUserRepository userRepository = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _stateMachine = stateMachine;
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        }

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
                var userRepositoryForCheck = scope.ServiceProvider.GetRequiredService<IUserRepository>();

                try
                {
                    var telegramUserId = user.Id.ToString();
                    var existingUser = await userRepositoryForCheck.GetByTelegramIdAsync(telegramUserId, cancellationToken);

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

                        // --- CONSTRUCT THE USER ENTITY COMPLETELY HERE ---
                        var userGuid = Guid.NewGuid();
                        var walletGuid = Guid.NewGuid();

                        var newUserEntityWithWallet = new Domain.Entities.User
                        {
                            Id = userGuid,
                            Username = effectiveUsername,
                            TelegramId = telegramUserId,
                            Email = $"{telegramUserId}@telegram.temp.user",
                            Level = UserLevel.Free,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                            EnableGeneralNotifications = true,
                            EnableVipSignalNotifications = false,
                            EnableRssNewsNotifications = true,
                            PreferredLanguage = user.LanguageCode ?? "en"
                        };

                        newUserEntityWithWallet.TokenWallet = new TokenWallet(
                            walletGuid,
                            userGuid,
                            0.0m,
                            true,
                            DateTime.UtcNow,
                            DateTime.UtcNow
                        );
                        // --- END OF CONSTRUCTION ---

                        // --- CALL THE USER SERVICE TO REGISTER AND CACHE ---
                        // The UserService will handle adding to the DB and then populating the Redis cache.
                        await userService.RegisterUserAsync(
                            new RegisterUserDto
                            {
                                Username = newUserEntityWithWallet.Username,
                                TelegramId = newUserEntityWithWallet.TelegramId,
                                Email = newUserEntityWithWallet.Email
                            },
                            cancellationToken,
                            userEntityToRegister: newUserEntityWithWallet
                        );

                        finalUsername = effectiveUsername;
                        scopedLogger.LogInformation("[BackgroundScope] New user {finalUsername} registered successfully and cached.", finalUsername);
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

        private InlineKeyboardMarkup GetMainMenuKeyboard()
        {
            return MenuCommandHandler.GetMainMenuMarkup().keyboard;
        }
    }
}