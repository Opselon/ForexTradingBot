using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Features.Forwarding.Services;
using Domain.Features.Forwarding.Entities;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Jobs
{
    public class ForwardingJob
    {
        private readonly IForwardingService _forwardingService;
        private readonly ILogger<ForwardingJob> _logger;

        public ForwardingJob(IForwardingService forwardingService, ILogger<ForwardingJob> logger)
        {
            _forwardingService = forwardingService ?? throw new ArgumentNullException(nameof(forwardingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ProcessMessageAsync(long sourceChannelId, long messageId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting message processing job for message {MessageId} from channel {ChannelId}", messageId, sourceChannelId);
            await _forwardingService.ProcessMessageAsync(sourceChannelId, messageId, cancellationToken);
            _logger.LogInformation("Completed message processing job for message {MessageId} from channel {ChannelId}", messageId, sourceChannelId);
        }
    }
} 