// File: src/Infrastructure/Services/HangfireJobScheduler.cs
using Application.Common.Interfaces;
using Hangfire;
using System.Linq.Expressions;

namespace Infrastructure.Services
{
    public class HangfireJobScheduler : INotificationJobScheduler
    {
        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            // Enqueue the job to be executed by Hangfire.
            // Hangfire will resolve TJob from the DI container when it's time to execute.
            return BackgroundJob.Enqueue<TJob>(methodCall);
        }

        public string Enqueue<TService>(Expression<Action<TService>> methodCall)
        {
            throw new NotImplementedException();
        }

    }
}