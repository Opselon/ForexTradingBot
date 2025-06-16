using System.Collections.Concurrent; // برای ConcurrentDictionary

namespace TelegramPanel.Application.States // ✅ Namespace صحیح
{
    /// <summary>
    /// وضعیت فعلی مکالمه کاربر و داده‌های مرتبط با آن.
    /// </summary>
    public class UserConversationState
    {

        public string? CurrentStateName { get; set; }
        public ConversationState CurrentState { get; set; } = ConversationState.None;
        public Dictionary<string, object> StateData { get; set; } = [];
    }

    /// <summary>
    /// اینترفیس برای سرویس مدیریت وضعیت مکالمه کاربر.
    /// </summary>
    public interface IUserConversationStateService
    {
        Task<UserConversationState?> GetAsync(long userId, CancellationToken cancellationToken = default);
        Task SetAsync(long userId, UserConversationState state, CancellationToken cancellationToken = default);
        Task ClearAsync(long userId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// پیاده‌سازی ساده در حافظه برای مدیریت وضعیت مکالمه کاربر.
    /// هشدار: این پیاده‌سازی برای Production مناسب نیست زیرا با ریستارت شدن برنامه، وضعیت‌ها از بین می‌روند.
    /// برای Production از پایگاه داده، Redis یا سرویس مشابه استفاده کنید.
    /// </summary>
    public class InMemoryUserConversationStateService : IUserConversationStateService
    {
        // استفاده از ConcurrentDictionary برای thread-safety پایه
        private readonly ConcurrentDictionary<long, UserConversationState> _userStates = new ConcurrentDictionary<long, UserConversationState>();

        public Task<UserConversationState?> GetAsync(long userId, CancellationToken cancellationToken = default)
        {
            _ = _userStates.TryGetValue(userId, out var state);
            return Task.FromResult(state);
        }

        public Task SetAsync(long userId, UserConversationState state, CancellationToken cancellationToken = default)
        {
            _userStates[userId] = state; // AddOrUpdate
            return Task.CompletedTask;
        }

        public Task ClearAsync(long userId, CancellationToken cancellationToken = default)
        {
            _ = _userStates.TryRemove(userId, out _);
            return Task.CompletedTask;
        }
    }
}