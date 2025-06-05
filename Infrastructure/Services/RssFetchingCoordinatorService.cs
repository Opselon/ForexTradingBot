// File: Infrastructure/Services/RssFetchingCoordinatorService.cs
#region Usings
using Application.Common.Interfaces; // For IRssSourceRepository, IRssReaderService
using Application.Interfaces;        // For IRssFetchingCoordinatorService
using Domain.Entities;               // For RssSource
using Hangfire;                      // For JobDisplayName, AutomaticRetry
using Microsoft.Extensions.Logging;
using Polly;                         // For Polly policies
using Polly.Retry;                   // For Retry policies
using System;
using System.Collections.Generic;    // For Dictionary, List
using System.Linq;                   // For Any(), Count()
using System.Threading;              // For CancellationToken, SemaphoreSlim
using System.Threading.Tasks;        // For Task, Task.WhenAll, Parallel.ForEachAsync
using Shared.Results; // Ensure this is included for Result<T>
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// Servis za koordinaciju i upravljanje procesom dohvaćanja i obrade RSS feedova.
    /// Ovaj servis dohvaća aktivne RSS feedove iz repozitorija i prosljeđuje svaki na obradu servisu <see cref="IRssReaderService"/>.
    /// Svaka operacija dohvaćanja feeda pojedinačno je zaštićena Pollyjem radi poboljšane otpornosti na prolazne pogreške.
    /// </summary>
    public class RssFetchingCoordinatorService : IRssFetchingCoordinatorService
    {
        private readonly IRssSourceRepository _rssSourceRepository;
        private readonly IRssReaderService _rssReaderService;
        private readonly ILogger<RssFetchingCoordinatorService> _logger;
        private readonly AsyncRetryPolicy _coordinatorRetryPolicy; // Renamed for clarity: this policy is for coordinator's external calls

        // Recommended: Limit concurrency to avoid overloading the VPS
        private const int MaxConcurrentFeedFetches = 2; // Match number of cores, or slightly more (e.g., 2-4)

        /// <summary>
        /// Konstruktor <see cref="RssFetchingCoordinatorService"/>.
        /// </summary>
        /// <param name="rssSourceRepository">Repozitorij za pristup RSS izvorima.</param>
        /// <param name="rssReaderService">Servis za čitanje i obradu RSS feedova.</param>
        /// <param name="logger">Logger za bilježenje informacija i pogrešaka.</param>
        public RssFetchingCoordinatorService(
            IRssSourceRepository rssSourceRepository,
            IRssReaderService rssReaderService,
            ILogger<RssFetchingCoordinatorService> logger)
        {
            _rssSourceRepository = rssSourceRepository ?? throw new ArgumentNullException(nameof(rssSourceRepository));
            _rssReaderService = rssReaderService ?? throw new ArgumentNullException(nameof(rssReaderService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Inicijalizacija Polly politike za ponovne pokušaje prolaznih pogrešaka.
            // Ova politika rukuje bilo kojom iznimkom koja dolazi iz `_rssReaderService.FetchAndProcessFeedAsync`
            // osim `OperationCanceledException` i `TaskCanceledException` koje označavaju namjerno otkazivanje.
            // Važno: `IRssReaderService` ima vlastite Polly politike za HTTP i DB pozive,
            // ova koordinatorska politika hvata i ponavlja ako cijeli `FetchAndProcessFeedAsync` ne uspije.
            _coordinatorRetryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException || ex is TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 2, // Manje ponavljanja na razini koordinatora, jer reader već ima svoja
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Eksponencijalni povratak: 2s, 4s
                                                                                                            // FIX CS8030: Change 'onRetry' to 'onRetryAsync' and ensure the lambda is 'async' and returns a Task.
                    onRetryAsync: (exception, timeSpan, retryAttempt, context) => // <--- CHANGED TO onRetryAsync
                    {
                        // Dodajte kontekst (npr. ID RSS izvora) za bolje bilježenje
                        string sourceInfo = context.Contains("RssSourceId") ? $" (Source: {context["RssSourceId"]})" : "";
                        _logger.LogWarning(exception,
                            "RssFetchingCoordinatorService: Transient error encountered while processing a single RSS feed{SourceInfo}. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            sourceInfo, timeSpan, retryAttempt, exception.Message);
                        return Task.CompletedTask; // <--- This is now valid because it's onRetryAsync
                    });
        }

        #region FetchAllActiveFeedsAsync (Public Hangfire Job)
        /// <summary>
        /// Dohvaća i obrađuje sve aktivne RSS feedove asinkrono.
        /// Ova metoda se izvršava kao Hangfire zadatak.
        /// Svaki feed se obrađuje paralelno s ograničenom konkurentnošću radi optimizacije resursa.
        /// </summary>
        /// <param name="cancellationToken">Token otkazivanja za otkazivanje operacije.</param>
        [JobDisplayName("Fetch All Active RSS Feeds - Coordinator")] // Prikazno ime za Hangfire nadzornu ploču
        [AutomaticRetry(Attempts = 0)] // Polly rukuje ponovnim pokušajima na razini obrade pojedinačnog feeda
        public async Task FetchAllActiveFeedsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[HANGFIRE JOB] Starting: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);

            var activeSources = (await _rssSourceRepository.GetActiveSourcesAsync(cancellationToken)).ToList();

            if (!activeSources.Any())
            {
                _logger.LogInformation("[HANGFIRE JOB] No active RSS sources found to fetch.");
                return;
            }
            _logger.LogInformation("[HANGFIRE JOB] Found {Count} active RSS sources to process. Processing with {Concurrency} concurrent fetches.", activeSources.Count(), MaxConcurrentFeedFetches);

            // Koristite SemaphoreSlim za ograničavanje konkurentnih zadataka.
            // To osigurava da ne preopteretimo VPS istovremenim HTTP/DB zahtjevima.
            using var semaphore = new SemaphoreSlim(MaxConcurrentFeedFetches);
            var processingTasks = new List<Task>();

            foreach (var source in activeSources)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("[HANGFIRE JOB] FetchAllActiveFeedsAsync job cancelled during source enumeration.");
                    break;
                }

                await semaphore.WaitAsync(cancellationToken); // Pričekajte mjesto u semaforu
                processingTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessSingleFeedWithLoggingAndRetriesAsync(source, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release(); // Oslobodite mjesto u semaforu
                    }
                }, cancellationToken));
            }

            // Pričekajte da se svi zadaci obrade ili dok se ne otkažu
            await Task.WhenAll(processingTasks);

            _logger.LogInformation("[HANGFIRE JOB] Finished: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);
        }
        #endregion

        #region ProcessSingleFeedWithLoggingAndRetriesAsync (Private Helper)
        /// <summary>
        /// Obrađuje pojedinačni RSS feed s detaljnim bilježenjem i ponovnim pokušajima na razini koordinatora.
        /// Ova metoda koristi Polly politiku definiranu u konstruktoru za zaštitu poziva servisa za čitanje feedova.
        /// </summary>
        /// <param name="source">RSS izvor za obradu.</param>
        /// <param name="cancellationToken">Token otkazivanja za otkazivanje operacije.</param>
        private async Task ProcessSingleFeedWithLoggingAndRetriesAsync(RssSource source, CancellationToken cancellationToken)
        {
            // Stvorite novi kontekst za Polly za ovaj pojedinačni pokušaj obrade feeda
            var pollyContext = new Context($"RssFeedFetch_{source.Id}");
            pollyContext["RssSourceId"] = source.Id; // Dodajte ID izvora u kontekst za bilježenje
            pollyContext["RssSourceName"] = source.SourceName; // Dodajte ime izvora u kontekst

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["RssSourceName"] = source.SourceName,
                ["RssSourceUrl"] = source.Url
            }))
            {
                _logger.LogInformation("Processing RSS source via coordinator...");
                try
                {
                    // Poziv servisa za čitanje RSS feedova, zaštićen Polly politikom koordinatora.
                    var result = await _coordinatorRetryPolicy.ExecuteAsync(async (ctx, ct) =>
                    {
                        // Proslijedite Pollyjev CancellationToken i kontekst na nižu razinu ako je potrebno
                        // IRssReaderService već prihvaća glavni cancellationToken.
                        // Ovdje koristimo samo glavni cancellationToken.
                        return await _rssReaderService.FetchAndProcessFeedAsync(source, ct);
                    }, pollyContext, cancellationToken);

                    if (result.Succeeded)
                    {
                        _logger.LogInformation("Successfully processed RSS source. New items: {NewItemCount}. Message: {ResultMessage}",
                            result.Data?.Count() ?? 0, result.SuccessMessage);
                    }
                    else
                    {
                        // AKO JE OVJDE BIO PROBLEM: 'FailureMessage'
                        _logger.LogWarning("Failed to process RSS source (non-exception failure). Errors: {Errors}. Final message: {FailureMessage}",
                            string.Join(", ", result.Errors), result.FailureMessage); // Corrected: using .FailureMessage
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("RSS feed processing for '{SourceName}' was cancelled by CancellationToken.", source.SourceName);
                }
                catch (Exception ex)
                {
                    // Uhvatite konačnu pogrešku ako Polly iscrpi sve ponovne pokušaje ili ako se pojavi iznimka
                    // koja nije obrađena Polly politikom.
                    _logger.LogError(ex, "Critical unhandled error while processing RSS source '{SourceName}' after all coordinator retries. Error: {ErrorMessage}",
                        source.SourceName, ex.Message);
                    // Ovdje se pogreška više ne baca kako bi se omogućilo nastavak obrade ostalih feedova.
                }
            }
        }
        #endregion
    }
}