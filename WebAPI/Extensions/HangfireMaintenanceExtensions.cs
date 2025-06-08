// File: WebAPI/Extensions/HangfireMaintenanceExtensions.cs

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Maintenance;

namespace WebAPI.Extensions
{
    public static class HangfireMaintenanceExtensions
    {
        /// <summary>
        /// Registers the IHangfireCleaner service for dependency injection.
        /// This method should be called when you are configuring your services.
        /// </summary>
        public static IServiceCollection AddHangfireCleaner(this IServiceCollection services)
        {
            services.AddScoped<IHangfireCleaner, HangfireCleaner>();
            return services;
        }

        /// <summary>
        /// Maps a secure POST endpoint to trigger the Hangfire cleanup process.
        /// This method should be called when you are configuring your application's request pipeline.
        /// THIS METHOD IS OPTIONAL if you are running the cleaner automatically at startup.
        /// </summary>
        public static IApplicationBuilder UseHangfirePurgeEndpoint(this IApplicationBuilder app, IHostEnvironment env)
        {
            // The 'UseEndpoints' method is the correct way to add a route to the pipeline.
            app.UseEndpoints(endpoints =>
            {
                var purgeEndpoint = endpoints.MapPost("/maintenance/hangfire-purge",
                    (IHangfireCleaner cleaner, IConfiguration config) =>
                    {
                        string connectionString = config.GetConnectionString("DefaultConnection")!;
                        cleaner.PurgeCompletedAndFailedJobs(connectionString);
                        return Results.Ok("Hangfire job data has been purged.");
                    });

                // In a production environment, require the user to be authenticated.
                if (env.IsProduction())
                {
                    purgeEndpoint.RequireAuthorization();
                }
            });

            return app;
        }
    }
}