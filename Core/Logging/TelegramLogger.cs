using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Core.Logging;

public class TelegramLogger : ILogger
{
    private readonly string _loggerPath;
    private readonly string _categoryName;

    public TelegramLogger(string loggerPath, string categoryName)
    {
        _loggerPath = loggerPath;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error || logLevel == LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var logEntry = new
        {
            Timestamp = DateTime.UtcNow,
            Level = logLevel.ToString(),
            Category = _categoryName,
            Message = message,
            Exception = exception?.ToString()
        };

        var jsonMessage = JsonSerializer.Serialize(logEntry);
        _ = LogAsync(jsonMessage);
    }

    private async Task LogAsync(string message)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _loggerPath,
                Arguments = $"\"{message}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            // Fallback to console logging if the logger process fails
            Console.WriteLine($"Failed to send log to Telegram: {ex.Message}");
        }
    }
} 