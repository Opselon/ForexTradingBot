// File: Infrastructure\Jobs\ForwardingJob.cs
using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services; // این using لازم است
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TL; // این using را اضافه کنید تا بتوانید از MessageEntity و Peer استفاده کنید

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

        // !!! این متد توسط Hangfire Scheduler صدا زده می‌شود. !!!
        // !!! پارامترهای ورودی باید دقیقا با پارامترهای IForwardingService.ProcessMessageAsync یکسان باشند. !!!
        public async Task ProcessMessageAsync(
       long sourceChannelIdForMatching,
       long originalMessageId,
       long rawSourcePeerIdForApi,
       string messageContent,
       TL.MessageEntity[]? messageEntities,
       Peer? senderPeerForFilter,
       List<InputMediaWithCaption>? mediaGroupItems, // CHANGED: Now a list
       CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "HANGFIRE_JOB: Starting job for MsgID: {OriginalMsgId}, SourceForMatching: {SourceForMatching}, RawSourceForApi: {RawSourceForApi}. Content Preview: '{ContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                originalMessageId, sourceChannelIdForMatching, rawSourcePeerIdForApi, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

            await _forwardingService.ProcessMessageAsync(
                sourceChannelIdForMatching,
                originalMessageId,
                rawSourcePeerIdForApi,
                messageContent,
                messageEntities,
                senderPeerForFilter,
                mediaGroupItems, // CHANGED: Now a list
                cancellationToken);

            _logger.LogInformation(
                "HANGFIRE_JOB: Completed job for MsgID: {OriginalMsgId}, SourceForMatching: {SourceForMatching}",
                originalMessageId, sourceChannelIdForMatching);
        }

        // Helper function to truncate strings for logging
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }
    }
}