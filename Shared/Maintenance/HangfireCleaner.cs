// File: Shared/Maintenance/HangfireCleaner.cs

using Dapper;
using Hangfire;
using Hangfire.States;
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

                using SqlConnection dbConnection = new(connectionString);
                int duplicatesRemoved = dbConnection.Execute(sql, commandTimeout: 300);

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
            catch (Exception)
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



        /// <summary>
        /// Purges ALL non-recurring jobs from Hangfire. This version is written to be
        /// compatible with a wider range of Hangfire versions and to resolve all
        /// listed compiler errors.
        /// </summary>
        /// <param name="connectionString">This parameter is included to match the required method signature but is not used.</param>
        /// <summary>
        /// Purges ALL non-recurring jobs from Hangfire. This version is written to be
        /// compatible with a wider range of Hangfire versions and to resolve all
        /// listed compiler errors.
        /// </summary>
        /// <param name="connectionString">This parameter is included to match the required method signature but is not used.</param>
        public void PurgeCompletedAndFailedJobs(string connectionString)
        {
            Console.WriteLine("--- Starting Hangfire Purge using Dapper (Recurring Job Safe) ---");

            const string schema = "[HangFire]";
            const int batchSize = 5000;

            // CRITICAL FIX: The 'Set' and 'Hash' tables are REMOVED from this list
            // as they are used to store recurring job data.
            var tablesToDeleteFrom = new[]
            {
            // Job statistics data (safe to delete)
            "AggregatedCounter",
            "Counter", 
            
            // Job-specific data (safe to delete)
            "JobParameter",
            "JobQueue",
            "List", 
            
            // Stale server data (safe to delete)
            "Server", 
            
            // Core job tables (must be last, in this order)
            "State",
            "Job"
        };

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    Console.WriteLine("Successfully connected to the database.");

                    foreach (var table in tablesToDeleteFrom)
                    {
                        string fullTableName = $"{schema}.[{table}]";
                        Console.WriteLine($"Starting batched delete for: {fullTableName}...");

                        string sql = $@"
                        WHILE 1 = 1
                        BEGIN
                            DELETE TOP ({batchSize}) FROM {fullTableName};
                            IF @@ROWCOUNT = 0 BREAK;
                        END";

                        connection.Execute(sql, commandTimeout: 300);

                        Console.WriteLine($"Successfully completed batched delete for {fullTableName}.");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("--- Dapper Hangfire Purge Operation Complete ---");
                Console.WriteLine("All job execution data, history, and statistics have been purged.");
                Console.WriteLine("Recurring job definitions have been preserved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred during the Dapper batch purge operation.");
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }
    }
}