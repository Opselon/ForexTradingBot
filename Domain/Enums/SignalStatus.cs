// File: Domain/Enums/SignalStatus.cs
namespace Domain.Enums
{
    /// <summary>
    /// Represents the possible statuses of a trading signal.
    /// </summary>
    public enum SignalStatus
    {
        /// <summary>
        /// The signal is newly created and awaiting validation or further processing before becoming active.
        /// </summary>
        Pending = 0,

        /// <summary>
        /// The signal is active and traders can consider acting upon it.
        /// </summary>
        Active = 1,

        /// <summary>
        /// The signal has reached its Take Profit target.
        /// </summary>
        ReachedTakeProfit = 2,

        /// <summary>
        /// The signal has hit its Stop Loss level.
        /// </summary>
        HitStopLoss = 3,

        /// <summary>
        /// The signal was cancelled manually before reaching TP or SL.
        /// </summary>
        Cancelled = 4,

        /// <summary>
        /// The signal has expired due to time or market conditions without hitting TP or SL.
        /// </summary>
        Expired = 5,

        /// <summary>
        /// The signal is closed for other reasons not covered by specific statuses.
        /// </summary>
        Closed = 6
    }
}