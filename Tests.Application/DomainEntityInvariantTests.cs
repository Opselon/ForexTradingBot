using Domain.Entities;
using Domain.Enums;

namespace Tests.Application;

public sealed class DomainEntityInvariantTests
{
    [Fact]
    public void User_constructor_establishes_safe_default_state()
    {
        var user = new User();

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal(UserLevel.Free, user.Level);
        Assert.True(user.EnableGeneralNotifications);
        Assert.False(user.EnableVipSignalNotifications);
        Assert.True(user.EnableRssNewsNotifications);
        Assert.Equal("en", user.PreferredLanguage);
        Assert.NotNull(user.Subscriptions);
        Assert.Empty(user.Subscriptions);
        Assert.NotNull(user.Transactions);
        Assert.Empty(user.Transactions);
        Assert.NotNull(user.Preferences);
        Assert.Empty(user.Preferences);
    }

    [Fact]
    public void User_value_constructor_normalizes_username_and_email_but_preserves_telegram_id()
    {
        var user = new User("  Alice  ", " 12345 ", "  ALICE@EXAMPLE.COM  ");

        Assert.Equal("Alice", user.Username);
        Assert.Equal(" 12345 ", user.TelegramId);
        Assert.Equal("alice@example.com", user.Email);
    }

    [Fact]
    public void User_value_constructor_rejects_missing_identity_fields()
    {
        Assert.Throws<ArgumentNullException>(() => new User(null!, "telegram", "email@example.com"));
        Assert.Throws<ArgumentNullException>(() => new User("user", null!, "email@example.com"));
        Assert.Throws<ArgumentNullException>(() => new User("user", "telegram", null!));
    }

    [Fact]
    public void Subscription_constructor_starts_pending_with_nonempty_identity()
    {
        var subscription = new Subscription();

        Assert.NotEqual(Guid.Empty, subscription.Id);
        Assert.Equal("Pending", subscription.Status);
        Assert.True(subscription.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public void Subscription_active_window_is_start_inclusive_and_end_exclusive()
    {
        var subscription = new Subscription
        {
            StartDate = DateTime.UtcNow.AddDays(-1),
            EndDate = DateTime.UtcNow.AddDays(1),
            Status = "Active"
        };

        Assert.True(subscription.IsCurrentlyActive);

        subscription.StartDate = DateTime.UtcNow.AddDays(1);
        Assert.False(subscription.IsCurrentlyActive);

        subscription.StartDate = DateTime.UtcNow.AddDays(-1);
        subscription.EndDate = DateTime.UtcNow.AddMilliseconds(50);
        Assert.True(subscription.IsCurrentlyActive);

        subscription.EndDate = DateTime.UtcNow.AddMilliseconds(-50);
        Assert.False(subscription.IsCurrentlyActive);
    }

    [Fact]
    public void Signal_constructor_establishes_pending_status_and_analysis_collection()
    {
        var signal = new Signal();

        Assert.NotEqual(Guid.Empty, signal.Id);
        Assert.Equal(SignalStatus.Pending, signal.Status);
        Assert.NotNull(signal.Analyses);
        Assert.Empty(signal.Analyses);
        Assert.True(signal.PublishedAt <= DateTime.UtcNow);
        Assert.False(signal.IsVipOnly);
    }
}
