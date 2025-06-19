#region Usings
// --- Aliases for Type Safety ---
using DomainUser = Domain.Entities.User;
using TGBotTypes = Telegram.Bot.Types;

using Application.Common.Interfaces;
using Application.DTOs;
using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Extensions; // For ILoggingSanitizer
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
#endregion

namespace TelegramPanel.Application.Pipeline
{
    /// <summary>
    /// A security-hardened, performant, and resilient middleware for authenticating Telegram updates.
    /// It performs a fast, cached check for user existence before fetching the full domain entity
    /// to populate the user context, ensuring both speed and correctness.
    /// </summary>
    public class AuthenticationMiddleware : ITelegramMiddleware
    {
        private readonly ILogger<AuthenticationMiddleware> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILoggingSanitizer _logSanitizer;

        public AuthenticationMiddleware(
            ILogger<AuthenticationMiddleware> logger,
            IServiceProvider serviceProvider,
            ILoggingSanitizer logSanitizer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logSanitizer = logSanitizer ?? throw new ArgumentNullException(nameof(logSanitizer));
        }

        public async Task InvokeAsync(TGBotTypes.Update update, TelegramPipelineDelegate next, CancellationToken cancellationToken)
        {
            var telegramUser = update.Message?.From ?? update.CallbackQuery?.From;

            if (telegramUser is null)
            {
                _logger.LogWarning("Update {UpdateId} received without a valid 'From' user. Halting pipeline.", update.Id);
                return;
            }

            using (_logger.BeginScope("AuthMiddleware: UpdateId={UpdateId}, UserId={UserId}", update.Id, telegramUser.Id))
            {
                if (update.Message?.Text?.StartsWith("/start") == true)
                {
                    _logger.LogInformation("Passing through public /start command to next handler.");
                    await next(update, cancellationToken);
                    return;
                }

                await AuthenticateAndProceedAsync(update, telegramUser, next, cancellationToken);
            }
        }

        private async Task AuthenticateAndProceedAsync(TGBotTypes.Update update, TGBotTypes.User telegramUser, TelegramPipelineDelegate next, CancellationToken cancellationToken)
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var userContext = scope.ServiceProvider.GetRequiredService<IUserContext>();
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>(); // Resolve repository for full entity fetch

            try
            {
                // --- STEP 1: FAST, CACHED DTO CHECK ---
                // Quickly verify if the user exists at all. This is great for fast-failing unauthorized users.
                var userDto = await userService.GetUserByTelegramIdAsync(telegramUser.Id.ToString(), cancellationToken);

                if (userDto is null)
                {
                    await HandleUnauthenticatedAccessAsync(telegramUser.Id, scope, cancellationToken);
                    return;
                }

                // --- STEP 2: FETCH THE FULL DOMAIN ENTITY ---
                // Now that we know the user is legitimate, fetch the complete, rich domain object.
                // This is the object that the rest of the application will use.
                var userEntity = await userRepository.GetByTelegramIdAsync(telegramUser.Id.ToString(), cancellationToken);

                if (userEntity is null)
                {
                    // This is a critical data inconsistency error. The DTO exists (maybe in a stale cache), but the user is gone from the DB.
                    _logger.LogCritical("Data Inconsistency: User DTO found for UserID {UserId}, but the full domain entity was not. Invalidate cache.", telegramUser.Id);
                    // Attempt to self-heal by clearing the stale cache entry.
                    var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
                    await cacheService.RemoveAsync($"user:telegram_id:{telegramUser.Id}");
                    await HandleCriticalFailureAsync(telegramUser.Id, scope, "A data consistency error occurred.", cancellationToken);
                    return;
                }

                // --- STEP 3: SET THE CONTEXT WITH THE CORRECT TYPE ---
                // The compiler error is now resolved because we are passing the correct `DomainUser` type.
                userContext.SetCurrentUser(userEntity);

                var sanitizedUsername = _logSanitizer.Sanitize(userEntity.Username);
                _logger.LogInformation("User ({SanitizedUsername}) authenticated successfully. Proceeding to next handler.", sanitizedUsername);

                await next(update, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical, unhandled error occurred during the authentication process.");
                await HandleCriticalFailureAsync(telegramUser.Id, scope, "A server error occurred during authentication.", cancellationToken);
            }
        }

        private async ValueTask HandleUnauthenticatedAccessAsync(long telegramId, AsyncServiceScope scope, CancellationToken cancellationToken)
        {
            _logger.LogWarning("Unauthenticated access attempt by UserID {UserId}. Access denied.", telegramId);
            var messageSender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();

            try
            {
                await messageSender.SendTextMessageAsync(
                    chatId: telegramId,
                    text: "⛔️ **Access Denied**\n\nYou are not authorized to use this command. Please use /start to register.",
                    parseMode: TGBotTypes.Enums.ParseMode.Markdown,
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send 'Access Denied' message to UserID {UserId}.", telegramId);
            }
        }

        private async ValueTask HandleCriticalFailureAsync(long telegramId, AsyncServiceScope scope, string message, CancellationToken cancellationToken)
        {
            var messageSender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();
            try
            {
                await messageSender.SendTextMessageAsync(
                    chatId: telegramId,
                    text: $"🤖 {message} Our team has been notified. Please try again later.",
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send critical failure message to UserID {UserId}.", telegramId);
            }
        }
    }
}