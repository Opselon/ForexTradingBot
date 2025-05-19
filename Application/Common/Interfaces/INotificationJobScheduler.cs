// File: Application/Common/Interfaces/INotificationJobScheduler.cs
#region Usings
using System.Linq.Expressions;
#endregion

namespace Application.Common.Interfaces // ✅ Namespace: Application.Common.Interfaces
{
    /// <summary>
    /// Defines a contract for a service that schedules background jobs for notifications.
    /// This abstracts the underlying job scheduling mechanism (e.g., Hangfire, RabbitMQ).
    /// </summary>
    public interface INotificationJobScheduler
    {
        /// <summary>
        /// Enqueues a fire-and-forget job that calls a method on the specified service type.
        /// </summary>
        /// <typeparam name="TService">The type of the service containing the method to execute.</typeparam>
        /// <param name="methodCall">An expression representing the method call to be executed.</param>
        /// <returns>A string identifier for the enqueued job (e.g., Hangfire Job ID).</returns>
        string Enqueue<TService>(Expression<Action<TService>> methodCall);

        /// <summary>
        /// Enqueues a fire-and-forget job that calls an asynchronous method on the specified service type.
        /// </summary>
        /// <typeparam name="TService">The type of the service containing the asynchronous method to execute.</typeparam>
        /// <param name="methodCall">An expression representing the asynchronous method call to be executed.</param>
        /// <returns>A string identifier for the enqueued job.</returns>
        string Enqueue<TService>(Expression<Func<TService, Task>> methodCall);

        //  می‌توانید متدهای دیگری برای زمان‌بندی با تأخیر (Schedule) یا تکرارشونده (Recurring) اضافه کنید
        // string Schedule<TService>(Expression<Action<TService>> methodCall, TimeSpan delay);
        // void AddOrUpdateRecurringJob<TService>(string recurringJobId, Expression<Action<TService>> methodCall, string cronExpression);
    }
}