// File: Infrastructure/Hangfire/HangfireNotificationJobScheduler.cs
#region Usings
using Application.Common.Interfaces; // ✅ برای INotificationJobScheduler
using Hangfire;                      // ✅ برای BackgroundJob.Enqueue (نیاز به بسته NuGet Hangfire.Core)
using System.Linq.Expressions;
#endregion

namespace Infrastructure.Hangfire // ✅ Namespace صحیح
{
    /// <summary>
    /// Implements INotificationJobScheduler using Hangfire for background job processing.
    /// </summary>
    public class HangfireNotificationJobScheduler : INotificationJobScheduler
    {
        /// <summary>
        /// Enqueues a fire-and-forget job to Hangfire.
        /// </summary>
        public string Enqueue<TService>(Expression<Action<TService>> methodCall)
        {
            //  BackgroundJob.Enqueue<TService>(methodCall) یک جاب را به صف پیش‌فرض Hangfire اضافه می‌کند.
            //  TService نوع اینترفیسی است که متد روی آن فراخوانی می‌شود.
            //  Hangfire مسئول resolve کردن پیاده‌سازی TService از DI و اجرای متد methodCall روی آن است.
            return BackgroundJob.Enqueue<TService>(methodCall);
        }

        /// <summary>
        /// Enqueues an asynchronous fire-and-forget job to Hangfire.
        /// </summary>
        public string Enqueue<TService>(Expression<Func<TService, Task>> methodCall)
        {
            return BackgroundJob.Enqueue<TService>(methodCall);
        }

        //  می‌توانید پیاده‌سازی‌های دیگری برای Schedule یا AddOrUpdateRecurringJob در اینجا اضافه کنید
        // public string Schedule<TService>(Expression<Action<TService>> methodCall, TimeSpan delay)
        // {
        //     return BackgroundJob.Schedule<TService>(methodCall, delay);
        // }

        // public void AddOrUpdateRecurringJob<TService>(string recurringJobId, Expression<Action<TService>> methodCall, string cronExpression)
        // {
        //     RecurringJob.AddOrUpdate<TService>(recurringJobId, methodCall, cronExpression);
        // }
    }
}