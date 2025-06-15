// using TelegramPanel.Domain.Interfaces; // Or wherever your IUserRepository is
using Application.Common.Interfaces;
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;     // For the User entity

namespace TelegramPanel.Application.Pipeline
{
    public class AuthenticationMiddleware : ITelegramMiddleware
    {
        private readonly ILogger<AuthenticationMiddleware> _logger;
        private readonly IUserService _userService;         // Returns DTOs
        private readonly IUserRepository _userRepository;  // <<--- NEW: Inject the repository
        private readonly IUserContext _userContext;
        private readonly ITelegramMessageSender _messageSender;

        public AuthenticationMiddleware(
            ILogger<AuthenticationMiddleware> logger,
            IUserService userService,
            IUserRepository userRepository, // <<--- NEW
            IUserContext userContext,
            ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository)); // <<--- NEW
            _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }

        public async Task InvokeAsync(Update update, TelegramPipelineDelegate next, CancellationToken cancellationToken)
        {
            long? userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;

            if (userId is null)
            {
                _logger.LogWarning("Update {UpdateId} received without a user ID. Update Type: {UpdateType}. Halting pipeline.", update.Id, update.Type);
                return;
            }

            if (update.Message?.Text?.StartsWith("/start") == true)
            {
                _logger.LogInformation("Passing through /start command for potential registration for UserID {UserId}", userId.Value);
                await next(update, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                // STEP 1: Use the service to check for EXISTENCE and get lightweight DTO.
                global::Application.DTOs.UserDto? userDto = await _userService.GetUserByTelegramIdAsync(userId.Value.ToString(), cancellationToken).ConfigureAwait(false);

                if (userDto is null)
                {
                    _logger.LogWarning("Unauthenticated access attempt by Telegram UserID {UserId}. Access denied.", userId.Value);
                    await _messageSender.SendTextMessageAsync(
                        chatId: userId.Value,
                        text: "Access Denied. ⛔️\nPlease register with the /start command first.",
                        cancellationToken: CancellationToken.None
                    ).ConfigureAwait(false);
                    return;
                }

                // --- THIS IS THE FIX ---
                // STEP 2: Now that user exists, fetch the full RICH DOMAIN ENTITY from the repository.
                Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(userId.Value.ToString(), cancellationToken).ConfigureAwait(false);

                // Add a sanity check in case of data inconsistency between services/DB.
                if (userEntity is null)
                {
                    _logger.LogCritical("Data Inconsistency: User DTO found for UserID {UserId}, but the full domain entity was not. Halting pipeline.", userId.Value);
                    await _messageSender.SendTextMessageAsync(
                       chatId: userId.Value,
                       text: "🤖 A server error occurred due to a data consistency issue. Please try again later.",
                       cancellationToken: CancellationToken.None
                   ).ConfigureAwait(false);
                    return;
                }

                // Now we have the correct type: 'Domain.Entities.User'
                // The compiler error is gone.
                _userContext.SetCurrentUser(userEntity);
                // --- END FIX ---

                string username = string.IsNullOrEmpty(userEntity.Username) ? "[no username]" : userEntity.Username;
                _logger.LogInformation("User {UserId} ({Username}) authenticated successfully. Proceeding with pipeline.", userEntity.TelegramId, username);

                await next(update, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical error occurred during authentication for UserID {UserId}. Halting pipeline.", userId.Value);
                await _messageSender.SendTextMessageAsync(
                    chatId: userId.Value,
                    text: "🤖 A server error occurred during authentication. Please try again later.",
                    cancellationToken: CancellationToken.None
                ).ConfigureAwait(false);
            }
        }
    }
}