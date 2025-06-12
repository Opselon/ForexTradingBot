// Place this in your Application/Interfaces folder
public interface INotificationRateLimiter
{
    /// <summary>
    /// Atomically checks if a user is over their notification limit for a given period.
    /// If they are not over the limit, it increments their count for the period.
    /// </summary>
    /// <param name="telegramUserId">The user's Telegram ID.</param>
    /// <param name="limit">The maximum number of notifications allowed.</param>
    /// <param name="period">The time window for the limit (e.g., 24 hours).</param>
    /// <returns>True if the user is over the limit and the notification should be skipped; otherwise, false.</returns>
    Task<bool> IsUserOverLimitAsync(long telegramUserId, int limit, TimeSpan period);
}