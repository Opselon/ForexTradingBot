using Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

public class DummyNotificationService : INotificationService
{
    private readonly ILogger<DummyNotificationService> _logger;
    public DummyNotificationService(ILogger<DummyNotificationService> logger) { _logger = logger; }
    public Task SendNotificationAsync(string recipientIdentifier, string message, bool useRichText = false, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[DUMMY NOTIFICATION] To: {Recipient}, Message: {Message}, RichText: {UseRichText}",
            recipientIdentifier, message.Length > 50 ? message[..50] + "..." : message, useRichText);
        return Task.CompletedTask;
    }
}
