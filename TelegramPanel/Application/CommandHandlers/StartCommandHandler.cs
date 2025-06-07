using Application.DTOs;              // برای RegisterUserDto و UserDto از پروژه اصلی Application
using Application.Interfaces;        // برای IUserService از پروژه اصلی Application
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces; // برای ITelegramCommandHandler
using TelegramPanel.Formatters;         // برای TelegramMessageFormatter
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;       // برای ITelegramMessageSender

namespace TelegramPanel.Application.CommandHandlers
{
    public class StartCommandHandler : ITelegramCommandHandler, ITelegramCallbackQueryHandler
    {
        private readonly ILogger<StartCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine? _stateMachine; // اختیاری، اگر نیاز به تغییر وضعیت دارید
        private readonly ITelegramBotClient _botClient; // Added ITelegramBotClient
        private readonly IServiceScopeFactory _scopeFactory;
        private const string ShowMainMenuCallback = "show_main_menu";

        public StartCommandHandler(
            ILogger<StartCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IServiceScopeFactory scopeFactory,
            ITelegramBotClient botClient, // Added ITelegramBotClient
            ITelegramStateMachine? stateMachine = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory)); // Assign it
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
            var message = callbackQuery.Message!;
            var user = callbackQuery.From;
            var chatId = message.Chat.Id;
            var messageId = message.MessageId;

            _logger.LogInformation("Handling '{Callback}' for UserID: {UserId}, editing message {MessageId} to become main menu.",
                ShowMainMenuCallback, user.Id, messageId);

            try
            {
                // 1. Answer the callback query immediately.
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);

                // 2. Prepare the personalized main menu text.
                var effectiveUsername = !string.IsNullOrWhiteSpace(user.Username) ? user.Username : user.FirstName;
                var welcomeText = $"🎉 *Welcome back, {TelegramMessageFormatter.EscapeMarkdownV2(effectiveUsername)}!*";
                var messageBody = GenerateWelcomeMessageBody(welcomeText, isExistingUser: true);
                var keyboard = GetMainMenuKeyboard();

                // 3. EDIT the current message to become the main menu. Use the DIRECT client for speed.
                await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: messageBody,
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken
                );

                // 4. Clear state if applicable.
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


        // New private helper to centralize the welcome message body creation
        private string GenerateWelcomeMessageBody(string welcomeHeader, bool isExistingUser)
        {
            return $"{welcomeHeader}\n\n" +
                   "Your trusted companion for gold trading signals and market analysis.\n\n" +
                   "📊 *Available Features:*\n" +
                   "• 📈 Real-time gold price alerts\n" +
                   "• 💎 Professional trading signals\n" +
                   "• 📰 Market analysis and insights\n" +
                   (isExistingUser ? "• 💼 Portfolio tracking\n• 🔔 Customizable notifications\n\n" :
                    "• 💼 Portfolio tracking\n\n") +
                   "Use the menu below or type /help for more information.";
        }


        // --- REWRITE THIS METHOD ---
        // --- REWRITE HandleStartCommand ---
        private async Task HandleStartCommand(Message message, CancellationToken cancellationToken)
        {
            var user = message.From!;
            var chatId = message.Chat.Id;

            _logger.LogInformation("FAST /start received for UserID: {UserId}. Sending initial generic welcome.", user.Id);

            Message? sentMessage;
            try
            {
                // STEP 1: Send the initial message DIRECTLY to get its ID back.
                // We use _botClient here to bypass the Hangfire queue for an immediate response.
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

            // STEP 2: Start the background work, passing the ID of the message we just sent.
            _ = Task.Run(async () =>
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<StartCommandHandler>>();
                    var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                    var stateMachine = scope.ServiceProvider.GetService<ITelegramStateMachine>();
                    var messageSender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>(); // Use queued sender for the edit

                    try
                    {
                        var telegramUserId = user.Id.ToString();
                        var existingUser = await userService.GetUserByTelegramIdAsync(telegramUserId, cancellationToken);

                        bool isNewRegistration = existingUser == null;
                        string finalUsername;

                        if (isNewRegistration)
                        {
                            // Logic to register the new user
                            var firstName = user.FirstName ?? "";
                            var lastName = user.LastName ?? "";
                            var username = user.Username;
                            string effectiveUsername = !string.IsNullOrWhiteSpace(username) ? username : $"{firstName} {lastName}".Trim();
                            if (string.IsNullOrWhiteSpace(effectiveUsername)) effectiveUsername = $"User_{telegramUserId}";

                            await userService.RegisterUserAsync(new RegisterUserDto { Username = effectiveUsername, TelegramId = telegramUserId, Email = $"{telegramUserId}@telegram.temp.user" }, cancellationToken);
                            finalUsername = effectiveUsername;
                            scopedLogger.LogInformation("[BackgroundScope] New user {finalUsername} registered successfully.", finalUsername);
                        }
                        else
                        {
                            finalUsername = existingUser!.Username;
                            scopedLogger.LogInformation("[BackgroundScope] Existing user {finalUsername} found.", finalUsername);
                            if (stateMachine != null) await stateMachine.ClearStateAsync(user.Id, cancellationToken);
                        }

                        // STEP 3: Edit the original message with personalized content.
                        // We can use the queued sender here, as a slight delay for the edit is acceptable.
                        await EditWelcomeMessageWithDetailsAsync(
                            chatId,
                            sentMessage.MessageId,
                            finalUsername,
                            !isNewRegistration, // isExistingUser is the opposite of isNewRegistration
                            messageSender, // Pass the scoped sender
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        scopedLogger.LogError(ex, "[BackgroundScope] Error during user processing or message edit for TelegramID {UserId}.", user.Id);
                    }
                }
            }, cancellationToken);
        }

        // --- NEW HELPER: For the initial, fast send ---
        private Task<Message> SendInitialWelcomeMessageAsync(long chatId, string firstName, CancellationToken cancellationToken)
        {
            var welcomeText = $"Hello {TelegramMessageFormatter.EscapeMarkdownV2(firstName)}! 👋\n\n" +
                              "🌟 *Welcome to Gold Market Trading Bot*\n\n" +
                              "Initializing your profile, please wait...";

            var keyboard = GetMainMenuKeyboard(); // Centralize keyboard creation

            // Use the direct _botClient to send and get the Message object back.
            return _botClient.SendMessage(
                 chatId: chatId,
                 text: welcomeText,
                 parseMode: ParseMode.Markdown,
                 replyMarkup: keyboard,
                 cancellationToken: cancellationToken);
        }

        // --- NEW HELPER: For the follow-up edit ---
        private Task EditWelcomeMessageWithDetailsAsync(long chatId, int messageId, string username, bool isExistingUser, ITelegramMessageSender messageSender, CancellationToken cancellationToken)
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
                              "Use the menu below or type /help for more information.";

            var keyboard = GetMainMenuKeyboard();

            // Use the provided message sender (which will be the queued one from the background scope)
            return messageSender.EditMessageTextAsync(
                chatId,
                messageId,
                messageBody,
                ParseMode.MarkdownV2,
                keyboard,
                cancellationToken);
        }

        // --- NEW HELPER: Centralize keyboard creation to avoid duplication ---
        private InlineKeyboardMarkup GetMainMenuKeyboard()
        {
            return MarkupBuilder.CreateInlineKeyboard(
               new[] {
        InlineKeyboardButton.WithCallbackData("📈 Gold Signals", MenuCommandHandler.SignalsCallbackData),
        InlineKeyboardButton.WithCallbackData("📊 Market Analysis", "market_analysis")
               },
               new[] {
        InlineKeyboardButton.WithCallbackData("💎 Subscribe", MenuCommandHandler.SubscribeCallbackData),
        InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData)
               },
               new[] {
        InlineKeyboardButton.WithCallbackData("👤 My Profile", MenuCommandHandler.ProfileCallbackData)
               }
           );
        }
    }
}