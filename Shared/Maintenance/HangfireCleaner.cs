// File: Shared/Maintenance/HangfireCleaner.cs

using Dapper;
using Hangfire;
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
            // ... (connection string check) ...
            _logger.LogInformation("Starting improved duplicate NewsItem cleanup based on Title and PublishedDate...");

            try
            {
                // ✅ NEW, MORE ROBUST SQL QUERY
                // This version defines a duplicate as items from the same source, with a
                // similar title, published around the same time. This is much more
                // effective for feeds that lack a reliable SourceItemId.
                const string sql = @"
WITH DuplicateCTE AS (
    SELECT
        Id,
        ROW_NUMBER() OVER(
            PARTITION BY
                RssSourceId,
                -- We partition by the first 200 characters of the title to catch identical headlines.
                CAST(Title AS NVARCHAR(200)),
                -- We also partition by the date part of the PublishedDate to group news from the same day.
                CAST(PublishedDate AS DATE)
            ORDER BY
                -- We keep the one that was published first, or entered our system first if times are identical.
                PublishedDate ASC,
                CreatedAt ASC
        ) AS RowNum
    FROM [dbo].[NewsItems]
)
DELETE FROM DuplicateCTE
WHERE RowNum > 1;
";

                using (var dbConnection = new SqlConnection(connectionString))
                {
                    var duplicatesRemoved = dbConnection.Execute(sql, commandTimeout: 300);

                    if (duplicatesRemoved > 0)
                    {
                        _logger.LogInformation("Successfully purged {DuplicateCount} duplicate NewsItem records based on title and date.", duplicatesRemoved);
                    }
                    else
                    {
                        _logger.LogInformation("No title-based duplicate NewsItem records were found to purge.");
                    }

                    return duplicatesRemoved;
                }
            }
            catch (Exception ex)
            {
                // ... error logging ...
                return 0;
            }
        }

        /*
         * For More speed
IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_NewsItems_ForDuplicateDetection' AND object_id = OBJECT_ID('[dbo].[NewsItems]'))
BEGIN
DROP INDEX [IX_NewsItems_ForDuplicateDetection] ON [dbo].[NewsItems];
PRINT 'Old index [IX_NewsItems_ForDuplicateDetection] was found and has been dropped.';
END
ELSE
BEGIN
PRINT 'Old index [IX_NewsItems_ForDuplicateDetection] was not found. No action taken.';
END
GO


-- Step 2: Create the new, optimized index.
-- This index is designed to quickly find duplicates based on the Title and PublishedDate.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_NewsItems_ForTitleBasedDuplicateDetection' AND object_id = OBJECT_ID('[dbo].[NewsItems]'))
BEGIN
CREATE NONCLUSTERED INDEX [IX_NewsItems_ForTitleBasedDuplicateDetection] ON [dbo].[NewsItems]
(
    [RssSourceId] ASC,
    [Title] ASC,
    [PublishedDate] ASC
)
INCLUDE([CreatedAt]); -- Including CreatedAt makes the sorting within duplicate groups more efficient.

PRINT 'New index [IX_NewsItems_ForTitleBasedDuplicateDetection] has been successfully created.';
END
ELSE
BEGIN
PRINT 'New index [IX_NewsItems_ForTitleBasedDuplicateDetection] already exists. No action taken.';
END
GO

         */



        public void PurgeCompletedAndFailedJobs(string connectionString)
        {
            var monitoringApi = JobStorage.Current.GetMonitoringApi();
            var succeeded = monitoringApi.SucceededJobs(0, int.MaxValue);

            foreach (var job in succeeded)
            {
                BackgroundJob.Delete(job.Key);
            }
        }
    }
}