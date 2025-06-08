// File: Shared/Maintenance/IHangfireCleaner.cs

namespace Shared.Maintenance
{
    /// <summary>
    /// Defines a contract for maintenance operations on Hangfire storage,
    /// such as purging old jobs to keep the dashboard and database responsive.
    /// </summary>
    public interface IHangfireCleaner
    {
        /// <summary>
        /// Deletes all jobs from the Succeeded and Failed sets in Hangfire.
        /// This is a destructive operation used to clean up storage.
        /// </summary>
        void PurgeCompletedAndFailedJobs(string connectionString);

        /// <summary>
        /// Finds and removes duplicate NewsItem records from the database,
        /// keeping only the most recently published one for each group of duplicates.
        /// </summary>
        /// <returns>The number of duplicate records that were removed.</returns>
        int PurgeDuplicateNewsItems(string connectionString);
    }
}