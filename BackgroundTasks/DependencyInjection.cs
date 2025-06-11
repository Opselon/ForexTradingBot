// File: BackgroundTasks/DependencyInjection.cs
using Application.Common.Interfaces;    // برای INotificationSendingService
using BackgroundTasks.Services;         // برای NotificationSendingService

namespace BackgroundTasks
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddBackgroundTasksServices(this IServiceCollection services)
        {
            //  رجیستر کردن Job Handler برای Hangfire
            //  طول عمر آن بستگی به وابستگی‌هایش دارد. اگر به سرویس‌های Scoped (مانند DbContext) نیاز ندارد،
            //  می‌تواند Transient یا Singleton باشد. اما چون به ITelegramMessageSender (Scoped) و IUserService (Scoped) وابسته است،
            //  باید Scoped باشد و Hangfire باید برای هر Job یک Scope ایجاد کند.
            //  (Hangfire به طور پیش‌فرض این کار را با استفاده از IServiceScopeFactory انجام می‌دهد).
            _ = services.AddScoped<INotificationSendingService, NotificationSendingService>();

            return services;
        }
    }
}