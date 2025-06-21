#region Usings
using Application.Common.Interfaces;
using Application.DTOs;
using Application.Interfaces;
// Explicitly use an alias for the domain entity User to avoid conflicts
using DomainUser = Domain.Entities.User;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Extensions;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
// Explicitly specify the Telegram.Bot.Types namespace for all its types
using TGBotTypes = Telegram.Bot.Types;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
using Domain.Entities;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Extensions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;
#endregion

namespace TelegramPanel.Application.CommandHandlers.Entry
{
    /// <summary>
    /// Handles the /start command with a focus on security, scalability, and resilience.
    /// It uses Redis for distributed locking to prevent race conditions and ensures all user input is sanitized.
    /// </summary>
    public class StartCommandHandler : ITelegramCommandHandler, ITelegramCallbackQueryHandler
    {
        private readonly ILogger<StartCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine? _stateMachine;
        private readonly ITelegramBotClient _botClient;
        private readonly IServiceScopeFactory _scopeFactory;
        private const string ShowMainMenuCallback = "show_main_menu";
        private readonly ILoggingSanitizer _logSanitizer;
        private readonly IUserRepository _userRepository; // Add this field

        public StartCommandHandler(
            ILoggingSanitizer logSanitizer,
            ILogger<StartCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IServiceScopeFactory scopeFactory,
            ITelegramBotClient botClient,
            IUserRepository userRepository, // <-- ADD THIS PARAMETER
            ITelegramStateMachine? stateMachine = null)
        {
            _logSanitizer = logSanitizer ?? throw new ArgumentNullException(nameof(logSanitizer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository)); // <-- ADD THIS ASSIGNMENT
            _stateMachine = stateMachine;
        }


        // --- FIX: Explicitly use Telegram.Bot.Types.Update ---
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                update.Message?.Text?.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase) == true
                ? true
                : update.Type == UpdateType.CallbackQuery &&
                update.CallbackQuery?.Data == ShowMainMenuCallback;
        }


        // --- FIX: Explicitly use Telegram.Bot.Types.Update ---
        public async Task HandleAsync(TGBotTypes.Update update, CancellationToken cancellationToken = default)
        {
            switch (update)
            {
                case { Message: { From: { } user, Chat: { } chat } }:
                    await HandleStartCommand(user, chat.Id, cancellationToken);
                    break;
                case { CallbackQuery: { From: { } user, Message: { } message } }:
                    await HandleShowMainMenuCallback(update.CallbackQuery, user, message, cancellationToken);
                    break;
                default:
                    _logger.LogWarning("StartCommandHandler received an update it cannot handle. UpdateType: {UpdateType}", update.Type);
                    break;
            }
        }
        

        private async Task HandleStartCommand(TGBotTypes.User telegramUser, long chatId, CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
            var registrationLockKey = $"lock:register:{telegramUser.Id}";

            string? lockToken = await cacheService.AcquireLockAsync(registrationLockKey, TimeSpan.FromSeconds(30));
            if (lockToken == null)
            {
                _logger.LogWarning("Registration for UserID {UserId} is already in progress (lock held). Ignoring duplicate /start request.", telegramUser.Id);
                return;
            }

            try
            {
                var sanitizedFirstName = _logSanitizer.Sanitize(telegramUser.FirstName);
                _logger.LogInformation("Lock acquired for UserID {UserId}. Sending initial welcome message to {SanitizedFirstName}.", telegramUser.Id, sanitizedFirstName);
                var sentMessage = await SendInitialWelcomeMessageAsync(chatId, telegramUser.FirstName, cancellationToken);

                _ = Task.Run(() => ProcessUserRegistrationAsync(telegramUser, sentMessage.MessageId, registrationLockKey, lockToken, cancellationToken), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send initial welcome message to UserID {UserId}. Releasing lock.", telegramUser.Id);
                await cacheService.ReleaseLockAsync(registrationLockKey, lockToken);
            }
        }


        // --- FIX: Explicitly use Telegram.Bot.Types.User ---
        private async Task ProcessUserRegistrationAsync(TGBotTypes.User telegramUser, int messageId, string lockKey, string lockToken, CancellationToken cancellationToken)
        {
            long userId = telegramUser.Id;

            using var scope = _scopeFactory.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
            var messageSender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();
            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<StartCommandHandler>>();
            var sanitizedUsernameForLogs = _logSanitizer.Sanitize(telegramUser.Username ?? telegramUser.FirstName);

            try
            {
                // The logic inside this method is now correct and uses scoped services or the main injected _userRepository
                var userCacheKey = $"user:telegram_id:{userId}";
                var cachedUserDto = await cacheService.GetAsync<UserDto>(userCacheKey);

                string finalUsername;
                bool isNewUser;

                if (cachedUserDto != null)
                {
                    scopedLogger.LogInformation("User {UserId} ({SanitizedUsername}) found in Redis cache. Skipping database checks.", userId, sanitizedUsernameForLogs);
                    finalUsername = cachedUserDto.Username;
                    isNewUser = false;
                }
                else
                {
                    // Use the repository injected into the class constructor for the DB check
                    var userFromDb = await _userRepository.GetByTelegramIdAsync(userId.ToString(), cancellationToken);

                    if (userFromDb == null)
                    {
                        scopedLogger.LogInformation("User {UserId} ({SanitizedUsername}) is confirmed new. Proceeding with registration.", userId, sanitizedUsernameForLogs);
                        var newUserEntity = CreateNewUserEntity(telegramUser);

                        await userService.RegisterUserAsync(
                            new RegisterUserDto { Username = newUserEntity.Username, TelegramId = newUserEntity.TelegramId, Email = newUserEntity.Email },
                            cancellationToken,
                            userEntityToRegister: newUserEntity
                        );

                        finalUsername = newUserEntity.Username;
                        isNewUser = true;
                        scopedLogger.LogInformation("User {UserId} ({SanitizedUsername}) registered and cached successfully.", userId, _logSanitizer.Sanitize(finalUsername));
                    }
                    else
                    {
                        finalUsername = userFromDb.Username;
                        isNewUser = false;
                        scopedLogger.LogInformation("Existing user {UserId} ({SanitizedUsername}) found in database. Cache will be populated by UserService.", userId, _logSanitizer.Sanitize(finalUsername));
                    }
                }

                if (!isNewUser && _stateMachine != null)
                {
                    await _stateMachine.ClearStateAsync(telegramUser.Id, cancellationToken);
                }

                await EditWelcomeMessageWithDetailsAsync(userId, messageId, finalUsername, !isNewUser, messageSender, cancellationToken);
            }
            catch (Exception ex)
            {
                scopedLogger.LogCritical(ex, "A critical, unhandled error occurred during the background registration process for UserID {UserId}.", userId);
                await messageSender.EditMessageTextAsync(userId, messageId, "An internal server error occurred. Please try again later.", cancellationToken: CancellationToken.None);
            }
            finally
            {
                await cacheService.ReleaseLockAsync(lockKey, lockToken);
                scopedLogger.LogTrace("Registration lock for UserID {UserId} released.", userId);
            }
        }

        // --- FIX: Use DomainUser alias and Telegram.Bot.Types.User explicitly ---
        private DomainUser CreateNewUserEntity(TGBotTypes.User telegramUser)
        {
            var telegramUserId = telegramUser.Id.ToString();
            string effectiveUsername = GetEffectiveUsername(telegramUser);
            var sanitizedUsername = _logSanitizer.Sanitize(effectiveUsername);

            var userGuid = Guid.NewGuid();
            var newUser = new DomainUser
            {
                Id = userGuid,
                Username = sanitizedUsername,
                TelegramId = telegramUserId,
                Email = $"{telegramUserId}@telegram.placeholder.email",
                Level = UserLevel.Free,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                EnableGeneralNotifications = true,
                EnableVipSignalNotifications = false,
                EnableRssNewsNotifications = true,
                PreferredLanguage = SanitizeLanguageCode(telegramUser.LanguageCode)
            };

            newUser.TokenWallet = new TokenWallet(Guid.NewGuid(), userGuid, 0.0m, true, DateTime.UtcNow, DateTime.UtcNow);
            return newUser;
        }


        // --- FIX: Explicitly use Telegram.Bot.Types.User ---
        private string GetEffectiveUsername(TGBotTypes.User telegramUser)
        {
            string? name = !string.IsNullOrWhiteSpace(telegramUser.Username)
                ? telegramUser.Username
                : $"{telegramUser.FirstName} {telegramUser.LastName}".Trim();

            return string.IsNullOrWhiteSpace(name) ? $"User_{telegramUser.Id}" : name;
        }

        private string SanitizeLanguageCode(string? langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return "en";
            return Regex.IsMatch(langCode, @"^[a-zA-Z]{2}(-[a-zA-Z]{2})?$") ? langCode : "en";
        }

        // In StartCommandHandler.cs

        private async Task HandleShowMainMenuCallback(TGBotTypes.CallbackQuery callbackQuery, TGBotTypes.User user, TGBotTypes.Message originalMessage, CancellationToken cancellationToken)
        {
            var chatId = originalMessage.Chat.Id;
            var messageId = originalMessage.MessageId;

            using var scope = _scopeFactory.CreateScope();
            var messageSender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();
            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<StartCommandHandler>>();

            scopedLogger.LogInformation("Handling '{Callback}' for UserID: {UserId}. Strategy: Delete and Send New.",
                ShowMainMenuCallback, user.Id);

            try
            {
                // Answer the callback to remove the "loading" state from the button.
                await messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                // --- START OF TARGETED FIX ---
                // 1. Delete the previous message, whatever it was (photo or text).
                await messageSender.DeleteMessageAsync(chatId, messageId, cancellationToken);

                // 2. Prepare the content for the new main menu.
                var effectiveUsername = GetEffectiveUsername(user);
                var welcomeText = $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(effectiveUsername)}!*";
                var messageBody = GenerateWelcomeMessageBody(welcomeText, isExistingUser: true);
                var keyboard = GetMainMenuKeyboard();

                // 3. Send the new, text-based main menu.
                await messageSender.SendTextMessageAsync(
                    chatId,
                    messageBody,
                    ParseMode.MarkdownV2,
                    keyboard,
                    cancellationToken
                );
                // --- END OF TARGETED FIX ---

                if (_stateMachine != null) await _stateMachine.ClearStateAsync(user.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                scopedLogger.LogError(ex, "A critical error occurred while handling '{Callback}' for UserID {UserId}",
                    ShowMainMenuCallback, user.Id);
            }
        }

        private Task<TGBotTypes.Message> SendInitialWelcomeMessageAsync(long chatId, string firstName, CancellationToken cancellationToken)
        {
            var sanitizedFirstName = _logSanitizer.Sanitize(firstName);
            var loadingCaption = $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(sanitizedFirstName)}! 👋\n\n" +
                                 "🌟 *Welcome to the Forex AI Analyzer*\n\n" +
                                 "Initializing your profile, please wait...";

            var photoUrl = TGBotTypes.InputFile.FromUri("https://i.postimg.cc/CL8sSt8h/Chat-GPT-Image-Jun-20-2025-01-07-32-AM.png");

            return _botClient.SendMessage(
                 chatId: chatId,
                 text: loadingCaption,
                 parseMode: ParseMode.Markdown,
                 replyMarkup: GetMainMenuKeyboard(),
                 cancellationToken: cancellationToken);
        }

        private Task EditWelcomeMessageWithDetailsAsync(long chatId, int messageId, string username, bool isExistingUser, ITelegramMessageSender messageSender, CancellationToken cancellationToken)
        {
            var sanitizedUsername = _logSanitizer.Sanitize(username);

            // Determine the final welcome header.
            var welcomeHeader = isExistingUser
                ? $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(sanitizedUsername)}!*"
                : $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(sanitizedUsername)}! 👋\n\n🌟 *Welcome to the Forex AI Analyzer*";

            // Generate the full message body, which will become the new caption.
            var finalCaption = GenerateWelcomeMessageBody(welcomeHeader, isExistingUser);

            // --- THIS IS THE FIX ---
            // Use EditMessageCaptionAsync to change the caption of a message that already has media.
            // We call _botClient directly as it's guaranteed to have this method.
            return messageSender.EditMessageTextAsync(
                chatId: chatId,
                messageId: messageId,
                text:finalCaption,
                parseMode: TGBotTypes.Enums.ParseMode.Markdown,
                replyMarkup: (InlineKeyboardMarkup)GetMainMenuKeyboard(), // Cast to the correct type
                cancellationToken: cancellationToken);
        }
        private string GenerateWelcomeMessageBody(string welcomeHeader, bool isExistingUser) =>
           $"{welcomeHeader}\n\nYour trusted companion for trading signals and market analysis.\n\n📊 *Available Features:*\n• 📈 Real-time alerts & signals\n• 💎 Professional trading tools\n• 📰 In-depth news analysis\n" +
           (isExistingUser ? "• 💼 Portfolio tracking\n• 🔔 Customizable notifications\n\n" : "• 💼 Portfolio tracking\n\n") +
           "Use the menu below or type /help for more information.";

        private InlineKeyboardMarkup GetMainMenuKeyboard() => MenuCommandHandler.GetMainMenuMarkup().keyboard;
    }
}