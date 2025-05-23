using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Core.Logging;

public static class TelegramLoggerExtensions
{
    public static ILoggingBuilder AddTelegramLogger(this ILoggingBuilder builder, string loggerPath)
    {
        builder.Services.AddSingleton<ILoggerProvider>(new TelegramLoggerProvider(loggerPath));
        return builder;
    }
} 