// File: Infrastructure/Services/RssFetchingCoordinatorService.cs
#region Usings
using Application.Common.Interfaces; // برای IRssSourceRepository, IRssReaderService
using Application.Interfaces;        // ✅ برای IRssFetchingCoordinatorService
using Domain.Entities;               // ✅ برای RssSource (در متد ProcessSingleFeedWithLoggingAsync)
using Hangfire;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;    // ✅ برای Dictionary
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Infrastructure.Services // ✅ Namespace صحیح
{
    public class RssFetchingCoordinatorService : IRssFetchingCoordinatorService
    {
        // ... (کد پیاده‌سازی که در پاسخ قبلی ارائه شد) ...
        private readonly IRssSourceRepository _rssSourceRepository;
        private readonly IRssReaderService _rssReaderService;
        private readonly ILogger<RssFetchingCoordinatorService> _logger;

        public RssFetchingCoordinatorService(
            IRssSourceRepository rssSourceRepository,
            IRssReaderService rssReaderService,
            ILogger<RssFetchingCoordinatorService> logger)
        {
            _rssSourceRepository = rssSourceRepository;
            _rssReaderService = rssReaderService;
            _logger = logger;
        }


        [JobDisplayName("Fetch All Active RSS Feeds - Coordinator")] // نام نمایشی برای داشبورد
        [AutomaticRetry(Attempts = 0)] // ما از Polly برای Retry در RssReaderService استفاده می‌کنیم
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

            foreach (var source in activeSources)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("[HANGFIRE JOB] FetchAllActiveFeedsAsync job cancelled.");
                    break;
                }
                await ProcessSingleFeedWithLoggingAsync(source, cancellationToken);
            }
            _logger.LogInformation("[HANGFIRE JOB] Finished: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);
        }

        private async Task ProcessSingleFeedWithLoggingAsync(RssSource source, CancellationToken cancellationToken)
        {
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RssSourceName"] = source.SourceName, ["RssSourceUrl"] = source.Url }))
            {
                _logger.LogInformation("Processing RSS source via coordinator...");
                try
                {
                    var result = await _rssReaderService.FetchAndProcessFeedAsync(source, cancellationToken);
                    if (result.Succeeded)
                    {
                        _logger.LogInformation("Successfully processed RSS source. New items: {NewItemCount}. Message: {ResultMessage}",
                            result.Data?.Count() ?? 0, result.SuccessMessage);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to process RSS source. Errors: {Errors}", string.Join(", ", result.Errors));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Critical unhandled error while RssFetchingCoordinator was processing RSS source.");
                }
            }
        }
    }
}