using Application.DTOs;              // برای RegisterUserDto و UserDto از پروژه اصلی Application
using Application.Interfaces;        // برای IUserService از پروژه اصلی Application
using Microsoft.Extensions.Logging;
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

        public StartCommandHandler(
            ILogger<StartCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IUserService userService,
            ITelegramStateMachine? stateMachine = null) // stateMachine را اختیاری کردم
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _stateMachine = stateMachine; // می‌تواند null باشد اگر رجیستر نشده یا لازم نیست
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message;
            if (message?.From == null)
            {
                _logger.LogWarning("StartCommand: Message or From user is null in UpdateID {UpdateId}.", update.Id);
                return;
            }

            var chatId = message.Chat.Id;
            var telegramUserId = message.From.Id.ToString();
            var firstName = message.From.FirstName ?? ""; // اطمینان از عدم null بودن
            var lastName = message.From.LastName ?? "";   // اطمینان از عدم null بودن
            var username = message.From.Username;

            string effectiveUsername = !string.IsNullOrWhiteSpace(username) ? username : $"{firstName} {lastName}".Trim();
            if (string.IsNullOrWhiteSpace(effectiveUsername))
            {
                effectiveUsername = $"User_{telegramUserId}"; // استفاده از _ برای جداسازی بهتر
            }

            _logger.LogInformation("Handling /start command for TelegramUserID: {TelegramUserId}, ChatID: {ChatId}, EffectiveUsername: {EffectiveUsername}",
                telegramUserId, chatId, effectiveUsername);

            try
            {
                //  مهم: مطمئن شوید IUserService به درستی پیاده‌سازی شده و IUserRepository را به درستی استفاده می‌کند.
                var existingUser = await _userService.GetUserByTelegramIdAsync(telegramUserId, cancellationToken);

                if (existingUser != null)
                {
                    _logger.LogInformation("Existing user {Username} (TelegramID: {TelegramId}) initiated /start.", existingUser.Username, telegramUserId);
                    var welcomeBackMessage = $"🎉 *Welcome back, {TelegramMessageFormatter.Bold(existingUser.Username)}!*\n\n" +
                                           "🌟 *Gold Market Trading Bot*\n\n" +
                                           "Your trusted companion for gold trading signals and market analysis.\n\n" +
                                           "📊 *Available Features:*\n" +
                                           "• 📈 Real-time gold price alerts\n" +
                                           "• 💎 Professional trading signals\n" +
                                           "• 📰 Market analysis and insights\n" +
                                           "• 💼 Portfolio tracking\n" +
                                           "• 🔔 Customizable notifications\n\n" +
                                           "Type /menu to see available options or /help for more information.";

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("📈 Gold Signals", MenuCommandHandler.SignalsCallbackData),
                            InlineKeyboardButton.WithCallbackData("📊 Market Analysis", "market_analysis")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("💎 VIP Signals", MenuCommandHandler.SubscribeCallbackData),
                            InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData)
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("📱 My Profile", MenuCommandHandler.ProfileCallbackData)
                        }
                    });

                    await _messageSender.SendTextMessageAsync(
                        chatId,
                        welcomeBackMessage,
                        ParseMode.MarkdownV2,
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken);

                    // پاک کردن وضعیت کاربر اگر در مکالمه‌ای بوده (اگر _stateMachine تزریق شده باشد)
                    if (_stateMachine != null)
                    {
                        await _stateMachine.ClearStateAsync(message.From.Id, cancellationToken);
                    }
                }
                else
                {
                    _logger.LogInformation("New user initiating /start. TelegramID: {TelegramId}, EffectiveUsername: {EffectiveUsername}. Registering...",
                        telegramUserId, effectiveUsername);

                    //  این ایمیل موقت است. در یک سناریوی واقعی باید راهی برای دریافت ایمیل واقعی پیدا کنید
                    //  (مثلاً با یک مرحله اضافی در مکالمه با کاربر با استفاده از StateMachine).
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

                    var welcomeMessage = $"Hello {TelegramMessageFormatter.Bold(newUser.Username)}! 👋\n\n" +
                                       "🌟 *Welcome to Gold Market Trading Bot*\n\n" +
                                       "Your trusted companion for gold trading signals and market analysis.\n\n" +
                                       "📊 *Available Features:*\n" +
                                       "• Real-time gold price alerts\n" +
                                       "• Professional trading signals\n" +
                                       "• Market analysis and insights\n" +
                                       "• Portfolio tracking\n\n" +
                                       "Type /menu to explore features or /help for assistance.";

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("📈 Gold Signals", MenuCommandHandler.SignalsCallbackData),
                            InlineKeyboardButton.WithCallbackData("📊 Market Analysis", "market_analysis")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("💎 VIP Signals", MenuCommandHandler.SubscribeCallbackData),
                            InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData)
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("📱 My Profile", MenuCommandHandler.ProfileCallbackData)
                        }
                    });

                    await _messageSender.SendTextMessageAsync(
                        chatId,
                        welcomeMessage,
                        ParseMode.MarkdownV2,
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling /start command for TelegramUserID {TelegramUserId}. EffectiveUsername: {EffectiveUsername}", telegramUserId, effectiveUsername);
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while processing your request. Please try again.", cancellationToken: cancellationToken);
            }
        }
    }
}