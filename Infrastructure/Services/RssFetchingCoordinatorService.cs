// File: Infrastructure/Services/RssFetchingCoordinatorService.cs
#region Usings
using Application.Common.Interfaces; // برای IRssSourceRepository, IRssReaderService
using Application.Interfaces;        // برای IRssFetchingCoordinatorService
using Domain.Entities;               // برای RssSource
using Hangfire;                      // برای JobDisplayName, AutomaticRetry
using Microsoft.Extensions.Logging;
using Polly;                         // اضافه کردن using برای Polly
using Polly.Retry;                   // اضافه کردن using برای سیاست‌های Retry
using System;
using System.Collections.Generic;    // برای Dictionary
using System.Linq;                   // برای Any(), Count()
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// سرویسی برای هماهنگی و مدیریت فرایند فچ کردن (fetch) و پردازش فیدهای RSS.
    /// این سرویس فیدهای RSS فعال را از ریپازیتوری بازیابی کرده و هر کدام را برای پردازش به <see cref="IRssReaderService"/> ارجاع می‌دهد.
    /// عملیات فچ کردن هر فید به صورت جداگانه با استفاده از Polly برای افزایش پایداری در برابر خطاهای گذرا محافظت می‌شود.
    /// </summary>
    public class RssFetchingCoordinatorService : IRssFetchingCoordinatorService
    {
        private readonly IRssSourceRepository _rssSourceRepository;
        private readonly IRssReaderService _rssReaderService;
        private readonly ILogger<RssFetchingCoordinatorService> _logger;
        private readonly AsyncRetryPolicy _retryPolicy; // فیلد برای سیاست Polly

        /// <summary>
        /// سازنده <see cref="RssFetchingCoordinatorService"/>.
        /// </summary>
        /// <param name="rssSourceRepository">ریپازیتوری برای دسترسی به منابع RSS.</param>
        /// <param name="rssReaderService">سرویس برای خواندن و پردازش فیدهای RSS.</param>
        /// <param name="logger">لاگر برای ثبت اطلاعات و خطاها.</param>
        public RssFetchingCoordinatorService(
            IRssSourceRepository rssSourceRepository,
            IRssReaderService rssReaderService,
            ILogger<RssFetchingCoordinatorService> logger)
        {
            _rssSourceRepository = rssSourceRepository ?? throw new ArgumentNullException(nameof(rssSourceRepository));
            _rssReaderService = rssReaderService ?? throw new ArgumentNullException(nameof(rssReaderService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // مقداردهی اولیه سیاست Polly برای تلاش مجدد در برابر خطاهای گذرا.
            // این سیاست هر Exception را مدیریت می‌کند به جز OperationCanceledException و TaskCanceledException
            // که نشان‌دهنده لغو عمدی عملیات هستند.
            _retryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        // لاگ‌گذاری در هنگام هر بار تلاش مجدد
                        _logger.LogWarning(exception,
                            "RssFetchingCoordinatorService: Transient error encountered while processing a single RSS feed. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }

        /// <summary>
        /// فچ کردن و پردازش تمام فیدهای RSS فعال به صورت ناهمزمان.
        /// این متد به عنوان یک وظیفه Hangfire اجرا می‌شود.
        /// هر فید به صورت جداگانه پردازش می‌شود، و خطاهای گذرا در سطح پردازش هر فید با Polly مدیریت می‌شوند.
        /// </summary>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات.</param>
        [JobDisplayName("Fetch All Active RSS Feeds - Coordinator")] // نام نمایشی برای داشبورد Hangfire
        [AutomaticRetry(Attempts = 0)] // Polly در سطح پردازش هر فید مدیریت می‌شود، پس Hangfire خودش تلاش مجدد نکند.
        public async Task FetchAllActiveFeedsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[HANGFIRE JOB] Starting: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);

            var activeSources = await _rssSourceRepository.GetActiveSourcesAsync(cancellationToken);

            if (!activeSources.Any())
            {
                _logger.LogInformation("[HANGFIRE JOB] No active RSS sources found to fetch.");
                return;
            }
            _logger.LogInformation("[HANGFIRE JOB] Found {Count} active RSS sources to process.", activeSources.Count());

            // پردازش تک تک منابع. خطاهای گذرا در متد ProcessSingleFeedWithLoggingAsync مدیریت می‌شوند.
            foreach (var source in activeSources)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("[HANGFIRE JOB] FetchAllActiveFeedsAsync job cancelled.");
                    break;
                }
                // هر فید به صورت مستقل پردازش می‌شود. اگر خطایی در یک فید رخ دهد،
                // این خطا مدیریت می‌شود و پردازش فیدهای بعدی ادامه می‌یابد.
                await ProcessSingleFeedWithLoggingAsync(source, cancellationToken);
            }

            _logger.LogInformation("[HANGFIRE JOB] Finished: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);
        }

        /// <summary>
        /// پردازش یک فید RSS خاص با لاگ‌گذاری مربوطه.
        /// این متد وظیفه اصلی خواندن و پردازش فید را به <see cref="IRssReaderService"/> واگذار می‌کند
        /// و عملیات فراخوانی <see cref="IRssReaderService.FetchAndProcessFeedAsync"/> را با Polly برای
        /// مدیریت خطاهای گذرا محافظت می‌کند.
        /// </summary>
        /// <param name="source">منبع RSS برای پردازش.</param>
        /// <param name="cancellationToken">توکن لغو برای لغو عملیات.</param>
        private async Task ProcessSingleFeedWithLoggingAsync(RssSource source, CancellationToken cancellationToken)
        {
            // ایجاد یک Scope برای لاگ‌گذاری تا اطلاعات منبع RSS (نام و URL) به طور خودکار به لاگ‌های این بلاک اضافه شوند.
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RssSourceName"] = source.SourceName, ["RssSourceUrl"] = source.Url }))
            {
                _logger.LogInformation("Processing RSS source via coordinator...");
                try
                {
                    // فراخوانی سرویس خواندن RSS برای فچ و پردازش فید.
                    // این فراخوانی توسط سیاست تلاش مجدد Polly محافظت می‌شود.
                    var result = await _retryPolicy.ExecuteAsync(async () =>
                    {
                        return await _rssReaderService.FetchAndProcessFeedAsync(source, cancellationToken);
                    });

                    if (result.Succeeded)
                    {
                        _logger.LogInformation("Successfully processed RSS source. New items: {NewItemCount}. Message: {ResultMessage}",
                            result.Data?.Count() ?? 0, result.SuccessMessage);
                    }
                    else
                    {
                        // اگر عملیات ناموفق بود (مثلاً نتیجه ناموفق از سرویس داخلی بازگشت، نه خطا)
                        _logger.LogWarning("Failed to process RSS source (non-exception failure). Errors: {Errors}", string.Join(", ", result.Errors));
                    }
                }
                catch (Exception ex)
                {
                    // گرفتن خطای نهایی اگر Polly تمام تلاش‌های مجدد را انجام دهد و باز هم موفق نشود،
                    // یا اگر خطایی رخ دهد که توسط سیاست Polly مدیریت نمی‌شود.
                    _logger.LogError(ex, "Critical unhandled error while processing RSS source '{SourceName}' after retries. Error: {ErrorMessage}",
                        source.SourceName, ex.Message);
                    // در اینجا دیگر خطایی را دوباره پرتاب نمی‌کنیم تا پردازش سایر فیدها مختل نشود.
                    // LogCritical کافی است.
                }
            }
        }
    }
}