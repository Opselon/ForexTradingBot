// File: Shared/Maintenance/HangfireCleaner.cs

using Dapper;
using Microsoft.Data.SqlClient; // Use Microsoft's official SQL Server library
using Microsoft.Extensions.Logging;

namespace Shared.Maintenance
{
    public class HangfireCleaner : IHangfireCleaner
    {
        private readonly ILogger<HangfireCleaner> _logger;

        public HangfireCleaner(ILogger<HangfireCleaner> logger)
        {
            _logger = logger;
        }
        // ✅ NEW METHOD IMPLEMENTATION
        public int PurgeDuplicateNewsItems(string connectionString)
        {
            // ... (check for connectionString) ...

            _logger.LogInformation("Starting duplicate NewsItem cleanup...");

            try
            {
                const string sql = @"
WITH DuplicateCTE AS (
    SELECT Id, ROW_NUMBER() OVER(PARTITION BY RssSourceId, SourceItemId ORDER BY PublishedDate DESC, CreatedAt DESC) AS RowNum
    FROM [dbo].[NewsItems]
    WHERE SourceItemId IS NOT NULL AND SourceItemId <> ''
)
DELETE FROM DuplicateCTE
WHERE RowNum > 1;
";

                using (var dbConnection = new SqlConnection(connectionString))
                {
                    // ✅ INCREASED TIMEOUT
                    // We give the command 5 minutes (300 seconds) to complete,
                    // which should be more than enough with the new index.
                    var duplicatesRemoved = dbConnection.Execute(sql, commandTimeout: 300);

                    _logger.LogInformation("Successfully purged {DuplicateCount} duplicate NewsItem records.", duplicatesRemoved);
                    return duplicatesRemoved;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical error occurred during the duplicate NewsItem cleanup.");
                return 0;
            }

            /*
             * For More speed
CREATE NONCLUSTERED INDEX [IX_NewsItems_ForDuplicateDetection] ON [dbo].[NewsItems]
(
	[RssSourceId] ASC,
	[SourceItemId] ASC,
	[PublishedDate] DESC,
	[CreatedAt] DESC
)
GO

             */
        }


        public void PurgeCompletedAndFailedJobs(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("Hangfire cleanup cannot proceed: Connection string is null or empty.");
                return;
            }

            _logger.LogInformation("Starting SQL-based Hangfire cleanup of Succeeded and Failed jobs...");

            try
            {
                // SQL commands to efficiently purge data and reset counters.
                // NOTE: We assume the default Hangfire schema name '[HangFire]'.
                // If you customized it, you must change it here too.
                const string sql = @"
                    TRUNCATE TABLE [HangFire].[JobParameter];
                    TRUNCATE TABLE [HangFire].[JobQueue];
                    TRUNCATE TABLE [HangFire].[List];
                    TRUNCATE TABLE [HangFire].[Set];
                    TRUNCATE TABLE [HangFire].[State];
                    TRUNCATE TABLE [HangFire].[AggregatedCounter];
                    TRUNCATE TABLE [HangFire].[Counter];
                    TRUNCATE TABLE [HangFire].[Hash];
                    DELETE FROM [HangFire].[Job];
                    DBCC CHECKIDENT ('[HangFire].[Job]', RESEED, 0);
                ";

                // ✅ CORRECTED: Create a new connection using the provided string.
                using (var dbConnection = new SqlConnection(connectionString))
                {
                    dbConnection.Execute(sql);
                }

                _logger.LogInformation("Hangfire Succeeded, Failed, and other completed job data has been purged successfully.");
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical error occurred during the SQL-based Hangfire job cleanup.");
            }
        }
    }
}