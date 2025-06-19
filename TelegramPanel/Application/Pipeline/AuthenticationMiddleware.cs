// using TelegramPanel.Domain.Interfaces; // Or wherever your IUserRepository is
using Application.Common.Interfaces;
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

// --- FIX: Added using statements for Domain entities and enums ---
using Domain.Entities;
using Domain.Enums;

namespace TelegramPanel.Application.Pipeline
{
    public class AuthenticationMiddleware : ITelegramMiddleware
    {
        private readonly ILogger<AuthenticationMiddleware> _logger;
        private readonly IUserService _userService;         // Returns DTOs
        private readonly IUserRepository _userRepository;  // Used for both reading and writing
        private readonly IUserContext _userContext;
        private readonly ITelegramMessageSender _messageSender;

        public AuthenticationMiddleware(
            ILogger<AuthenticationMiddleware> logger,
            IUserService userService,
            IUserRepository userRepository,
            IUserContext userContext,
            ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }

        public async Task InvokeAsync(Update update, TelegramPipelineDelegate next, CancellationToken cancellationToken)
        {
            var telegramUser = update.Message?.From ?? update.CallbackQuery?.From;

            if (telegramUser is null)
            {
                _logger.LogWarning("Update {UpdateId} received without a user ID. Update Type: {UpdateType}. Halting pipeline.", update.Id, update.Type);
                return;
            }

            var telegramId = telegramUser.Id.ToString();
            var username = telegramUser.Username ?? telegramUser.FirstName;

            // --- START OF FIX: The /start command logic is now fully handled here ---
            if (update.Message?.Text?.StartsWith("/start") == true)
            {
                _logger.LogInformation("Handling /start command for UserID {UserId} ({Username})", telegramId, username);

                // Step 1: Check if the user is already registered to avoid duplicates.
                bool userExists = await _userRepository.ExistsByTelegramIdAsync(telegramId, cancellationToken);

                if (userExists)
                {
                    _logger.LogInformation("User {UserId} ({Username}) is already registered. Sending welcome back message.", telegramId, username);
                    await _messageSender.SendTextMessageAsync(
                        chatId: telegramUser.Id,
                        text: $"Welcome back, {username}! You are already registered. ✅",
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                    // Stop processing here; the /start command's job is done.
                    return;
                }

                // Step 2: User does not exist, so let's register them.
                _logger.LogInformation("New user registration started for UserID {UserId} ({Username})", telegramId, username);

                try
                {
                    // Create the rich domain entity for the new user
                    var newUser = new Domain.Entities.User
                    {
                        Id = Guid.NewGuid(),
                        Username = username,
                        TelegramId = telegramId,
                        // Use a placeholder email. It's better than null.
                        Email = $"{username ?? telegramId}@telegram.local",
                        Level = UserLevel.Free, // Default to Free tier
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        // Set sensible defaults for notification settings
                        EnableGeneralNotifications = true,
                        EnableVipSignalNotifications = false,
                        EnableRssNewsNotifications = true,
                        PreferredLanguage = telegramUser.LanguageCode ?? "en"
                    };

                    // Create and associate a token wallet, as required by the AddAsync logic
                    newUser.TokenWallet = TokenWallet.Create(newUser.Id);

                    // Step 3: Add the new user to the database
                    await _userRepository.AddAsync(newUser, cancellationToken);
                    _logger.LogInformation("Successfully registered new user {UserId} ({Username}) with ID {DomainId}", telegramId, username, newUser.Id);

                    // Step 4: Send a confirmation message
                    await _messageSender.SendTextMessageAsync(
                        chatId: telegramUser.Id,
                        text: $"Welcome, {username}! 🎉 You have been successfully registered.",
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Failed to register new user {UserId} ({Username})", telegramId, username);
                    await _messageSender.SendTextMessageAsync(
                        chatId: telegramUser.Id,
                        text: "🤖 A server error occurred during registration. Please try again later.",
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                }
                // Registration is complete, stop pipeline execution for this update.
                return;
            }
            // --- END OF FIX ---


            // This part handles all OTHER commands for ALREADY REGISTERED users
            try
            {
                // Use the lightweight DTO check first (good practice)
                var userDto = await _userService.GetUserByTelegramIdAsync(telegramId, cancellationToken).ConfigureAwait(false);

                if (userDto is null)
                {
                    _logger.LogWarning("Unauthenticated access attempt by Telegram UserID {UserId}. Access denied.", telegramId);
                    await _messageSender.SendTextMessageAsync(
                        chatId: telegramUser.Id,
                        text: "Access Denied. ⛔️\nPlease register with the /start command first.",
                        cancellationToken: CancellationToken.None
                    ).ConfigureAwait(false);
                    return;
                }

                // Now fetch the full, rich domain entity to pass to the context
                var userEntity = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken).ConfigureAwait(false);

                if (userEntity is null)
                {
                    _logger.LogCritical("Data Inconsistency: User DTO found for UserID {UserId}, but the full domain entity was not. Halting pipeline.", telegramId);
                    await _messageSender.SendTextMessageAsync(
                       chatId: telegramUser.Id,
                       text: "🤖 A server error occurred due to a data consistency issue. Please try again later.",
                       cancellationToken: CancellationToken.None
                   ).ConfigureAwait(false);
                    return;
                }

                // Set the current user context for other parts of the application to use
                _userContext.SetCurrentUser(userEntity);

                _logger.LogInformation("User {UserId} ({Username}) authenticated successfully. Proceeding with pipeline.", userEntity.TelegramId, userEntity.Username);

                await next(update, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical error occurred during authentication for UserID {UserId}. Halting pipeline.", telegramId);
                await _messageSender.SendTextMessageAsync(
                    chatId: telegramUser.Id,
                    text: "🤖 A server error occurred during authentication. Please try again later.",
                    cancellationToken: CancellationToken.None
                ).ConfigureAwait(false);
            }
        }
    }
}