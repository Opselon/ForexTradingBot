// File: TelegramPanel/Application/CommandHandlers/NewsNotificationCallbackHandler.cs
#region Usings
using Application.Common.Interfaces; // برای IUserSignalPreferenceRepository, IAppDbContext, IUserRepository
using Application.Interfaces;        // برای IUserService
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
#endregion

namespace TelegramPanel.Application.CommandHandlers
{
    public class NewsNotificationCallbackHandler : ITelegramCallbackQueryHandler 
    {
        #region Private Readonly Fields
        private readonly ILogger<NewsNotificationCallbackHandler> _logger;
        private readonly ITelegramBotClient _botClient;
        private readonly IUserService _userService; // برای دریافت User.Id (Guid)
        private readonly IUserSignalPreferenceRepository _userPrefsRepository;
        private readonly ISignalCategoryRepository _categoryRepository; // برای دریافت نام دسته
        private readonly IAppDbContext _appDbContext; // برای SaveChangesAsync
        #endregion

        #region Public Callback Data Constants
        //  این ثابت‌ها باید با مقادیری که در NotificationSendingService استفاده شده، یکی باشند
        public const string SubscribeToCategoryPrefix = "news_sub_cat_";   // + CategoryId
        public const string UnsubscribeFromCategoryPrefix = "news_unsub_cat_"; // + CategoryId
        #endregion

        #region Constructor
        public NewsNotificationCallbackHandler(
            ILogger<NewsNotificationCallbackHandler> logger,
            ITelegramBotClient botClient,
            IUserService userService,
            IUserSignalPreferenceRepository userPrefsRepository,
            ISignalCategoryRepository categoryRepository,
            IAppDbContext appDbContext)
        {
            _logger = logger;
            _botClient = botClient;
            _userService = userService;
            _userPrefsRepository = userPrefsRepository;
            _categoryRepository = categoryRepository;
            _appDbContext = appDbContext;
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data != null &&
                   (update.CallbackQuery.Data.StartsWith(SubscribeToCategoryPrefix) ||
                    update.CallbackQuery.Data.StartsWith(UnsubscribeFromCategoryPrefix));
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            var message = callbackQuery.Message!;
            var fromUser = callbackQuery.From;

            var chatId = message.Chat.Id;
            var telegramUserId = fromUser.Id;
            var callbackData = callbackQuery.Data!;

            //  پاسخ اولیه به CallbackQuery
            await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, "Updating your preference...");

            //  دریافت User.Id (Guid) از طریق telegramUserId (long)
            var userDto = await _userService.GetUserByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userDto == null)
            {
                _logger.LogWarning("User not found for TelegramID {TelegramUserId} while handling news preference callback.", telegramUserId);
                await _botClient.EditMessageReplyMarkup(chatId, message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); // حذف دکمه‌ها
                await _botClient.SendMessage(chatId, "Error: Could not find your user profile. Please /start again.", cancellationToken: cancellationToken);
                return;
            }
            Guid systemUserId = userDto.Id;

            try
            {
                Guid categoryId;
                bool subscribeAction = false;

                if (callbackData.StartsWith(SubscribeToCategoryPrefix))
                {
                    if (!Guid.TryParse(callbackData.Substring(SubscribeToCategoryPrefix.Length), out categoryId))
                    {
                        _logger.LogWarning("Invalid CategoryID in subscribe callback: {CallbackData}", callbackData);
                        return;
                    }
                    subscribeAction = true;
                }
                else if (callbackData.StartsWith(UnsubscribeFromCategoryPrefix))
                {
                    if (!Guid.TryParse(callbackData.Substring(UnsubscribeFromCategoryPrefix.Length), out categoryId))
                    {
                        _logger.LogWarning("Invalid CategoryID in unsubscribe callback: {CallbackData}", callbackData);
                        return;
                    }
                    subscribeAction = false;
                }
                else
                {
                    _logger.LogWarning("Unknown news preference callback data: {CallbackData}", callbackData);
                    return;
                }

                var category = await _categoryRepository.GetByIdAsync(categoryId, cancellationToken);
                if (category == null)
                {
                    _logger.LogWarning("Category with ID {CategoryId} not found for news preference update.", categoryId);
                    await _botClient.EditMessageText(chatId, message.MessageId, "Error: The selected category no longer exists.", replyMarkup: null, cancellationToken: cancellationToken);
                    return;
                }

                string actionMessage;
                if (subscribeAction)
                {
                    //  بررسی اینکه آیا کاربر از قبل عضو نشده (اگرچه دکمه نباید نمایش داده می‌شد)
                    if (!await _userPrefsRepository.IsUserSubscribedToCategoryAsync(systemUserId, categoryId, cancellationToken))
                    {
                        await _userPrefsRepository.AddAsync(new UserSignalPreference { UserId = systemUserId, CategoryId = categoryId, CreatedAt = DateTime.UtcNow }, cancellationToken);
                        await _appDbContext.SaveChangesAsync(cancellationToken);
                        actionMessage = $"You are now subscribed to news from: *{TelegramMessageFormatter.EscapeMarkdownV2(category.Name)}*";
                        _logger.LogInformation("User {SystemUserId} subscribed to Category {CategoryId} ('{CategoryName}') via news notification.", systemUserId, categoryId, category.Name);
                    }
                    else
                    {
                        actionMessage = $"You are already subscribed to: *{TelegramMessageFormatter.EscapeMarkdownV2(category.Name)}*";
                    }
                }
                else // Unsubscribe action
                {
                    bool deleted = await _userPrefsRepository.DeleteAsync(systemUserId, categoryId, cancellationToken); //  این متد باید در Repository شما تغییر کند تا SaveChanges را انجام دهد یا bool برگرداند
                    if (deleted) //  اگر DeleteAsync در Repository خودش SaveChanges را انجام می‌دهد
                    {
                        await _appDbContext.SaveChangesAsync(cancellationToken); //  اگر DeleteAsync فقط علامت می‌زند
                        actionMessage = $"You have unsubscribed from news from: *{TelegramMessageFormatter.EscapeMarkdownV2(category.Name)}*";
                        _logger.LogInformation("User {SystemUserId} unsubscribed from Category {CategoryId} ('{CategoryName}') via news notification.", systemUserId, categoryId, category.Name);
                    }
                    else
                    {
                        actionMessage = $"You were not subscribed to: *{TelegramMessageFormatter.EscapeMarkdownV2(category.Name)}*";
                    }
                }

                //  پیام اصلی را با یک پیام تأیید ویرایش کنید و دکمه‌ها را حذف کنید.
                //  یا می‌توانید دکمه‌ها را به حالت جدید آپدیت کنید (کمی پیچیده‌تر).
                await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: message.MessageId,
                    text: $"{message.Text}\n\n{actionMessage}", //  متن خبر اصلی + پیام تأیید
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: null, // حذف دکمه‌های Subscribe/Unsubscribe پس از عمل
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing news notification preference callback for UserID {SystemUserId}, Data: {CallbackData}", systemUserId, callbackData);
                //  پیام خطا به کاربر (اختیاری)
                // await _messageSender.SendTextMessageAsync(chatId, "An error occurred while updating your preference.", cancellationToken: cancellationToken);
            }
        }
        #endregion

        #region Helper Methods
        private async Task AnswerCallbackQuerySilentAsync(string callbackQueryId, CancellationToken cancellationToken, string? text = null, bool showAlert = false)
        {
            try { await _botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert, cancellationToken: cancellationToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to answer cbq {Id}", callbackQueryId); }
        }
        #endregion
    }
}