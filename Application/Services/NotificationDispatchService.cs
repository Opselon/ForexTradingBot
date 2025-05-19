// File: Application/Services/NotificationDispatchService.cs
#region Usings
// Standard .NET & NuGet
// Project specific: Application Layer

using Application.Common.Interfaces;    // برای IUserRepository, (اختیاری IUserSignalPreferenceRepository), INotificationJobScheduler
using Application.DTOs.Notifications;   // برای NotificationJobPayload, NotificationButton
using Application.Interfaces;           // برای INotificationDispatchService
// Project specific: Domain Layer
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Shared.Extensions; // برای NewsItem, User
using System.Text; // برای StringBuilder (اگر پیام را اینجا می‌سازید، که بهتر است در SendingService باشد)
#endregion

namespace Application.Services
{
    /// <summary>
    /// Service responsible for identifying target recipients for notifications and dispatching
    /// these notification requests to a background job scheduler.
    /// It ensures that users receive relevant updates based on their preferences and subscription status.
    /// </summary>
    public class NotificationDispatchService : INotificationDispatchService
    {
        #region Private Readonly Fields
        private readonly IUserRepository _userRepository;
        // private readonly IUserSignalPreferenceRepository _userPrefsRepository; // اگر نیاز به فیلتر بر اساس دسته‌بندی خبر دارید
        private readonly INotificationJobScheduler _jobScheduler; // Abstraction for Hangfire or other queueing systems
        private readonly ILogger<NotificationDispatchService> _logger;
        private readonly INewsItemRepository _newsItemRepository;
        #endregion

        #region Constructor
        public NotificationDispatchService(INewsItemRepository newsItemRepository,
            IUserRepository userRepository,
            // IUserSignalPreferenceRepository userPrefsRepository,
            INotificationJobScheduler jobScheduler,
            ILogger<NotificationDispatchService> logger)
        {
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            // _userPrefsRepository = userPrefsRepository ?? throw new ArgumentNullException(nameof(userPrefsRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        #endregion

        #region INotificationDispatchService Implementation






        /// <summary>
        /// Dispatches notifications for a new <see cref="NewsItem"/>.
        /// It retrieves users who have enabled RSS news notifications and, if the news is category-specific
        /// or VIP, further filters users based on their preferences and subscription status.
        /// For each eligible user, a <see cref="NotificationJobPayload"/> is created and enqueued
        /// for asynchronous sending via <see cref="INotificationSendingService"/>.
        /// </summary>
        public async Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {

            var newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
            if (newsItem == null)
            {
                _logger.LogError("DispatchNewsNotificationAsync called with null NewsItem.");
                return;
            }

            using (_logger.BeginScope(new Dictionary<string, object?>
                   {
                       ["NewsItemId"] = newsItem.Id,
                       ["NewsTitle"] = newsItem.Title.Truncate(50)
                   }))
            {
                _logger.LogInformation("Starting notification dispatch for news item.");

                // ۱. استخراج اطلاعات لازم از newsItem برای فیلتر کردن کاربران
                //  این فیلدها باید در NewsItem.cs تعریف شده باشند
                Guid? newsItemCategoryId = newsItem.AssociatedSignalCategoryId; //  اگر خبر به دسته‌بندی خاصی لینک شده
                bool isVipNews = newsItem.IsVipOnly;                            //  اگر خبر فقط برای VIP هاست

                IEnumerable<User> targetUsers;
                try
                {
                    //  فراخوانی متد UserRepository که قبلاً با هم آپدیت کردیم
                    targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                        newsItemCategoryId,
                        isVipNews,
                        cancellationToken
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving target users for news item ID {NewsItemId}.", newsItem.Id);
                    return;
                }

                if (!targetUsers.Any())
                {
                    _logger.LogInformation("No target users found for news item ID {NewsItemId} based on their preferences/subscriptions.", newsItem.Id);
                    return;
                }

                _logger.LogInformation("Found {UserCount} target users for news item ID {NewsItemId}.", targetUsers.Count(), newsItem.Id);

                int dispatchedCount = 0;
                foreach (var user in targetUsers)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Notification dispatch process cancelled. {DispatchedCount} jobs were enqueued.", dispatchedCount);
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(user.TelegramId) || !long.TryParse(user.TelegramId, out long telegramUserId))
                    {
                        _logger.LogWarning("User {UserId} (Username: {Username}) has an invalid or missing TelegramId. Skipping notification.", user.Id, user.Username);
                        continue;
                    }

                    //  فیلتر نهایی بر اساس تنظیمات برگزیده دسته‌بندی کاربر (اگر خبر دسته‌بندی دارد)
                    //  این بخش در GetUsersForNewsNotificationAsync هم انجام شده، اما برای اطمینان مضاعف یا منطق پیچیده‌تر می‌توان اینجا هم داشت.
                    //  اگر GetUsersForNewsNotificationAsync به درستی کار می‌کند، این بخش اضافی است.
                    /*
                    if (newsItemCategoryId.HasValue)
                    {
                        var userPreferences = await _userPrefsRepository.GetPreferencesByUserIdAsync(user.Id, cancellationToken);
                        if (userPreferences.Any() && !userPreferences.Any(p => p.CategoryId == newsItemCategoryId.Value))
                        {
                            _logger.LogDebug("User {UserId} (TelegramID: {TelegramId}) is not subscribed to category {CategoryId} for NewsItem {NewsItemId}. Skipping.",
                                user.Id, telegramUserId, newsItemCategoryId.Value, newsItem.Id);
                            continue;
                        }
                    }
                    */

                    // ۲. ساخت پیام (متن اصلی از NewsItem، فرمت‌بندی نهایی در NotificationSendingService)
                    var messageTextBuilder = new StringBuilder();
                    string Truncate(string text, int maxLength)
                    {
                        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
                    }

                    //  استفاده از ایموجی مناسب برای نوع خبر یا منبع
                    var title = newsItem.Title?.Trim() ?? "Untitled";
                    var source = newsItem.SourceName?.Trim() ?? "Unknown Source";
                    var summary = Truncate(newsItem.Summary, 250);
                    var link = newsItem.Link?.Trim();

                    messageTextBuilder.AppendLine($"*📢 {title}*");
                    messageTextBuilder.AppendLine($"_📰 Source: {source}_");

                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        messageTextBuilder.AppendLine($"\n_{summary}_");
                    }

                    if (!string.IsNullOrWhiteSpace(link))
                    {
                        messageTextBuilder.AppendLine($"\n[🔗 Read Full Article]({link})");
                    }

                    var payload = new NotificationJobPayload
                    {
                        TargetTelegramUserId = telegramUserId,
                        MessageText = messageTextBuilder.ToString(),
                        UseMarkdown = true,
                        ImageUrl = newsItem.ImageUrl,
                        NewsItemId = newsItem.Id, // ✅ اضافه شد
                        NewsItemSignalCategoryId = newsItem.AssociatedSignalCategoryId, // ✅ اضافه شد
                        NewsItemSignalCategoryName = newsItem.AssociatedSignalCategory?.Name, // ✅ اضافه شد (نیاز به Include در خواندن NewsItem)
                        Buttons = new List<NotificationButton>
                        {
                            new NotificationButton { Text = "Read More", CallbackDataOrUrl = newsItem.Link, IsUrl = true }
                            //  دکمه‌های Subscribe/Unsubscribe در NotificationSendingService بر اساس وضعیت کاربر اضافه می‌شوند
                        },
                        CustomData = new Dictionary<string, string> { { "NewsItemId", newsItem.Id.ToString() } }
                    };

                    try
                    {
                        // ۳. قرار دادن Job در صف Hangfire
                        string jobId = _jobScheduler.Enqueue<INotificationSendingService>(service =>
                            service.SendNotificationAsync(payload, CancellationToken.None)); // CancellationToken.None برای خود جاب

                        _logger.LogInformation("Enqueued notification job {JobId} for UserID {SystemUserId} (TelegramID: {TelegramId}) for NewsItem {NewsItemId}.",
                            jobId, user.Id, telegramUserId, newsItem.Id);
                        dispatchedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to enqueue notification job for UserID {SystemUserId} (TelegramID: {TelegramId}) for NewsItem {NewsItemId}.",
                            user.Id, telegramUserId, newsItem.Id);
                    }
                }
                _logger.LogInformation("Finished dispatching notifications for NewsItem ID {NewsItemId}. Total jobs enqueued: {DispatchedCount}", newsItem.Id, dispatchedCount);
            }
        }

        // ... (متد EscapeMarkdown) ...
        private string EscapeMarkdown(string? text) // این متد باید کامل‌تر شود برای MarkdownV2
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("`", "\\`").Replace("]", "\\]");
            //  برای MarkdownV2 کامل‌تر: از TelegramMessageFormatter.EscapeMarkdownV2 استفاده کنید اگر در دسترس است
            //  یا اینکه این متد را در یک کلاس کمکی مشترک قرار دهید.
        }
    }
    #endregion
}

    //  در IUserRepository (Application/Common/Interfaces/IUserRepository.cs) باید متدی شبیه به این اضافه شود:
    /*
    public interface IUserRepository
    {
        // ... سایر متدها ...
        Task<IEnumerable<User>> GetUsersWithNotificationSettingAsync(
            Expression<Func<User, bool>> notificationPredicate, //  مثلاً u => u.EnableRssNewsNotifications
            CancellationToken cancellationToken = default);

        //  یا یک متد اختصاصی‌تر:
        Task<IEnumerable<User>> GetUsersForRssNewsNotificationAsync(Guid? relevantCategoryId, bool? isVipContent, CancellationToken cancellationToken);
    }
    */

    //  در پیاده‌سازی UserRepository (Infrastructure/Persistence/Repositories/UserRepository.cs):
    /*
    public async Task<IEnumerable<User>> GetUsersWithNotificationSettingAsync(
        Expression<Func<User, bool>> notificationPredicate,
        CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .Where(u => u.IsActive) //  فقط کاربران فعال (اگر فیلد IsActive دارید)
            .Where(notificationPredicate)
            .ToListAsync(cancellationToken);
    }
    */
    
