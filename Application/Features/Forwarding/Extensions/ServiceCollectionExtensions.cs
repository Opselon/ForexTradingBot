using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Features.Forwarding.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddForwardingServices(this IServiceCollection services)
        {
            // Register services

            services.AddScoped<IForwardingService, ForwardingService>();
            services.AddScoped<MessageProcessingService>();
            return services;
        }
    }
}