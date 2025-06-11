// --- START OF FILE: Application/Interfaces/IAdminService.cs ---

using Application.DTOs.Admin; // For the detailed DTO

namespace Application.Interfaces
{
    /// <summary>
    /// Defines a contract for administrative services, such as fetching stats,
    /// user data, and lists for broadcasting.
    /// </summary>
    public interface IAdminService
    {
        /// <summary>
        /// Gets dashboard statistics, including total user and news item counts.
        /// </summary>
        Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a list of all active user Telegram IDs for broadcasting.
        /// </summary>
        /// <returns>A list of numeric Telegram chat IDs.</returns>
        Task<List<long>> GetAllActiveUserChatIdsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Finds a single user by their unique Telegram ID and compiles a detailed DTO
        /// with related information like subscriptions and transactions.
        /// </summary>
        /// <param name="telegramId">The Telegram ID of the user to look up.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A detailed DTO for the admin panel, or null if the user is not found.</returns>
        Task<AdminUserDetailDto?> GetUserDetailByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default);

        Task<string> ExecuteRawSqlQueryAsync(string sqlQuery, CancellationToken cancellationToken = default);

    }
}