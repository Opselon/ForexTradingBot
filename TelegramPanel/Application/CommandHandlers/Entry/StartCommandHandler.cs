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
        private readonly ITelegramBotClient _botClient;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILoggingSanitizer _logSanitizer;
        private const string ShowMainMenuCallback = "show_main_menu";

        public StartCommandHandler(
            ILogger<StartCommandHandler> logger,
            IServiceScopeFactory scopeFactory,
            ITelegramBotClient botClient,
            ILoggingSanitizer logSanitizer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logSanitizer = logSanitizer ?? throw new ArgumentNullException(nameof(logSanitizer));
        }

        // --- FIX: Explicitly use Telegram.Bot.Types.Update ---
        public bool CanHandle(TGBotTypes.Update update) =>
            (update.Type == TGBotTypes.Enums.UpdateType.Message && update.Message?.Text?.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase) == true) ||
            (update.Type == TGBotTypes.Enums.UpdateType.CallbackQuery && update.CallbackQuery?.Data == ShowMainMenuCallback);

        // --- FIX: Explicitly use Telegram.Bot.Types.Update ---
        public async Task HandleAsync(TGBotTypes.Update update, CancellationToken cancellationToken = default)
        {
            switch (update)
            {
                // --- FIX: Explicitly use Telegram.Bot.Types for Message and User ---
                case { Message: { From: { } user, Chat: { } chat } }:
                    await HandleStartCommand(user, chat.Id, cancellationToken);
                    break;
                // --- FIX: Explicitly use Telegram.Bot.Types for CallbackQuery, User, and Message ---
                case { CallbackQuery: { From: { } user, Message: { } message } }:
                    await HandleShowMainMenuCallback(update.CallbackQuery, user, message, cancellationToken);
                    break;
                default:
                    _logger.LogWarning("StartCommandHandler received an update it cannot handle. UpdateID: {UpdateId}", update.Id);
                    break;
            }
        }

        // --- FIX: Explicitly use Telegram.Bot.Types.User ---
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
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<StartCommandHandler>>();
            var sanitizedUsernameForLogs = _logSanitizer.Sanitize(telegramUser.Username ?? telegramUser.FirstName);

            try
            {
                // --- UPGRADE: CACHE-FIRST STRATEGY ---
                var userCacheKey = $"user:telegram_id:{userId}";
                UserDto? cachedUserDto = await cacheService.GetAsync<UserDto>(userCacheKey);

                string finalUsername;
                bool isNewUser;

                if (cachedUserDto != null)
                {
                    // --- CACHE HIT: FAST PATH FOR EXISTING USERS ---
                    scopedLogger.LogInformation("User {UserId} ({SanitizedUsername}) found in Redis cache. Skipping database checks.", userId, sanitizedUsernameForLogs);
                    finalUsername = cachedUserDto.Username;
                    isNewUser = false; // User is not new
                }
                else
                {
                    // --- CACHE MISS: PROCEED WITH DATABASE LOGIC ---
                    scopedLogger.LogInformation("User {UserId} not found in cache. Checking database.", userId);
                    var userFromDb = await userRepository.GetByTelegramIdAsync(userId.ToString(), cancellationToken);

                    if (userFromDb == null)
                    {
                        // User is genuinely new.
                        scopedLogger.LogInformation("User {UserId} ({SanitizedUsername}) is confirmed new. Proceeding with registration.", userId, sanitizedUsernameForLogs);
                        var newUserEntity = CreateNewUserEntity(telegramUser);

                        // The UserService will save to the DB and then populate the cache.
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
                        // User exists in DB but not cache. This is a "warm-up" scenario.
                        finalUsername = userFromDb.Username;
                        isNewUser = false;
                        scopedLogger.LogInformation("Existing user {UserId} ({SanitizedUsername}) found in database. Cache will be populated by UserService.", userId, _logSanitizer.Sanitize(finalUsername));
                        // We can proactively warm up the cache here as well.
                        // Note: The UserService's GetUserByTelegramIdAsync already does this, but for clarity:
                        // var dto = _mapper.Map<UserDto>(userFromDb);
                        // await cacheService.SetAsync(userCacheKey, dto, TimeSpan.FromHours(1));
                    }
                }

                // This part is now common for all paths.
                if (!isNewUser)
                {
                    var stateMachine = scope.ServiceProvider.GetService<ITelegramStateMachine>();
                    if (stateMachine != null) await stateMachine.ClearStateAsync(userId, cancellationToken);
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

        // --- FIX: Explicitly use Telegram.Bot.Types for all parameters ---
        private async Task HandleShowMainMenuCallback(TGBotTypes.CallbackQuery callbackQuery, TGBotTypes.User user, TGBotTypes.Message message, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Handling '{Callback}' for UserID: {UserId}", ShowMainMenuCallback, user.Id);
            try
            {
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                var effectiveUsername = GetEffectiveUsername(user);
                var welcomeText = $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(effectiveUsername)}!*";
                var messageBody = GenerateWelcomeMessageBody(welcomeText, isExistingUser: true);
                var keyboard = GetMainMenuKeyboard();
                await _botClient.EditMessageText(message.Chat.Id, message.MessageId, messageBody, parseMode: TGBotTypes.Enums.ParseMode.MarkdownV2, replyMarkup: keyboard, cancellationToken: cancellationToken);

                var stateMachine = _scopeFactory.CreateScope().ServiceProvider.GetService<ITelegramStateMachine>();
                if (stateMachine != null) await stateMachine.ClearStateAsync(user.Id, cancellationToken);
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified")) { /* Ignore */ }
            catch (Exception ex) { _logger.LogError(ex, "Error handling '{Callback}' for UserID {UserId}", ShowMainMenuCallback, user.Id); }
        }

        private Task<TGBotTypes.Message> SendInitialWelcomeMessageAsync(long chatId, string firstName, CancellationToken cancellationToken)
        {
            var sanitizedFirstName = _logSanitizer.Sanitize(firstName);
            // This is the initial "loading" caption.
            var loadingCaption = $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(sanitizedFirstName)}! 👋\n\n" +
                                 "🌟 *Welcome to the Forex AI Analyzer*\n\n" +
                                 "Initializing your profile, please wait...";

            // This creates an InputFile object from the URL you provided.
            var photoUrl = TGBotTypes.InputFile.FromUri("https://i.postimg.cc/CL8sSt8h/Chat-GPT-Image-Jun-20-2025-01-07-32-AM.png");

            // Use SendPhotoAsync instead of SendMessageAsync
            return _botClient.SendPhoto(
                 chatId: chatId,
                 photo: photoUrl,
                 caption: loadingCaption, // Text now goes into the 'caption' parameter
                 parseMode: TGBotTypes.Enums.ParseMode.Markdown,
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
            return _botClient.EditMessageCaption(
                chatId: chatId,
                messageId: messageId,
                caption: finalCaption, // The new text goes into the 'caption' parameter
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