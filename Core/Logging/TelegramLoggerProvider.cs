using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Core.Logging;

public class TelegramLoggerProvider : ILoggerProvider
{
    private readonly string _loggerPath;

    public TelegramLoggerProvider(string loggerPath)
    {
        _loggerPath = loggerPath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TelegramLogger(_loggerPath, categoryName);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
} 