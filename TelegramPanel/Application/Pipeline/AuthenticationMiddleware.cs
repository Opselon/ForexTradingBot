// using TelegramPanel.Domain.Interfaces;
using Application.Common.Interfaces;
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
using Domain.Entities; // Keep this for IUserContext

namespace TelegramPanel.Application.Pipeline
{
    public class AuthenticationMiddleware : ITelegramMiddleware
    {
        private readonly ILogger<AuthenticationMiddleware> _logger;
        private readonly IUserService _userService;
        private readonly IUserRepository _userRepository;
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

            // --- THE FIX IS HERE ---
            // The middleware's only job for /start is to let it pass through.
            // The actual registration is handled by StartCommandHandler.
            if (update.Message?.Text?.StartsWith("/start") == true)
            {
                _logger.LogInformation("Passing through /start command for UserID {UserId} to the command handler.", telegramId);
                await next(update, cancellationToken).ConfigureAwait(false);
                return; // Stop the middleware here after passing the command along.
            }
            // --- END OF FIX ---


            // This logic now ONLY runs for commands OTHER THAN /start.
            try
            {
                // Lightweight DTO check first
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

                // Fetch the full domain entity to populate the context for downstream handlers.
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