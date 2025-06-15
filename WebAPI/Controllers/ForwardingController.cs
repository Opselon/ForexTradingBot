using Domain.Features.Forwarding.Entities; // For ForwardingRule entity used in GET/POST/PUT rules
using Hangfire;
using Infrastructure.Jobs; // For ForwardingJob
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers
{
    /// <summary>
    /// Request model for processing a message via API.
    /// </summary>
    public class ProcessMessageApiRequest
    {
        /// <summary>
        /// ID of the source channel (for rule matching, e.g., -100xxxx).
        /// </summary>
        public long SourceChannelId { get; set; }

        /// <summary>
        /// ID of the message to process.
        /// </summary>
        public long MessageId { get; set; }

        /// <summary>
        /// Raw ID of the source peer that Telegram API can use (e.g., -100xxxx or positive ID).
        /// This is crucial for ForwardingJobActions to resolve the source.
        /// Optional: If not provided, SourceChannelId might be used as a fallback.
        /// </summary>
        public long RawSourcePeerIdForApi { get; set; }

        // Optionally, if you want manual triggers to also apply text edits,
        // you would add these fields here and let the API caller provide them.
        // public string? MessageContent { get; set; }
        // public List<CustomEntityModel>? MessageEntities { get; set; } // You'd need a CustomEntityModel
        // public long? SenderUserIdForFilter { get; set; } // If sender is a user
        // public long? SenderChatIdForFilter { get; set; } // If sender is a chat/channel
    }

    /// <summary>
    /// Request model for creating a forwarding rule.
    /// </summary>
    public class CreateForwardingRuleRequest
    {
        /// <summary>
        /// Name of the forwarding rule.
        /// </summary>
        public required string RuleName { get; set; }

        /// <summary>
        /// ID of the source channel.
        /// </summary>
        public long SourceChannelId { get; set; }

        /// <summary>
        /// ID of the target channel.
        /// </summary>
        public long TargetChannelId { get; set; }
        // Note: To create a full rule via API, you'd need to include EditOptions and FilterOptions here.
        // For simplicity, this basic version is kept.
    }


    /// <summary>
    /// Controller for managing message forwarding rules and processing.
    /// </summary>
    [ApiController]
    [Route("api/v2/[controller]")]
    [Authorize]
    public class ForwardingController : ControllerBase
    {
        private readonly IForwardingService _forwardingService;
        private readonly ILogger<ForwardingController> _logger;

        public ForwardingController(IForwardingService forwardingService, ILogger<ForwardingController> logger)
        {
            _forwardingService = forwardingService ?? throw new ArgumentNullException(nameof(forwardingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }




        /// <summary>
        /// Manually triggers background processing for a specific message.
        /// </summary>
        [HttpPost("process/background")]
        public IActionResult ProcessMessageViaApi([FromBody] ProcessMessageApiRequest request)
        {
            if (request == null)
            {
                _logger.LogWarning("CONTROLLER.ProcessMessageViaApi: Received null request.");
                return BadRequest("Invalid request: Request body is null.");
            }

            long peerIdForJob = request.RawSourcePeerIdForApi;

            if (peerIdForJob == 0)
            {
                if (request.SourceChannelId != 0)
                {
                    _logger.LogWarning("CONTROLLER.ProcessMessageViaApi: RawSourcePeerIdForApi is zero in request, using SourceChannelId ({SourceChannelId}) as fallback for API peer ID.", request.SourceChannelId);
                    peerIdForJob = request.SourceChannelId;
                }
                else
                {
                    _logger.LogError("CONTROLLER.ProcessMessageViaApi: Invalid request. Both SourceChannelId and RawSourcePeerIdForApi are zero.");
                    return BadRequest("Invalid request: SourceChannelId for rule matching must be provided, and RawSourcePeerIdForApi should be provided or derivable from SourceChannelId.");
                }
            }
            if (request.SourceChannelId == 0)
            {
                _logger.LogError("CONTROLLER.ProcessMessageViaApi: Invalid request. SourceChannelId for rule matching cannot be zero.");
                return BadRequest("Invalid request: SourceChannelId for rule matching must be provided and non-zero.");
            }


            _logger.LogInformation("CONTROLLER.ProcessMessageViaApi: Enqueuing ForwardingJob. SourceChannelId (for matching): {SourceChannelId}, MessageId: {MessageId}, RawSourcePeerIdForApi (for job): {PeerIdForJob}",
                request.SourceChannelId, request.MessageId, peerIdForJob);

            _ = BackgroundJob.Enqueue<ForwardingJob>(job =>
                job.ProcessMessageAsync(
                    request.SourceChannelId,
                    request.MessageId,
                    peerIdForJob,
                    string.Empty,                  // messageContent
                    null,                          // messageEntities
                    null,                          // senderPeerForFilter
                    null,                          // NEW: inputMediaToSend (pass null here)
                    CancellationToken.None));      // CancellationToken
            return Ok($"Message processing job enqueued for message ID {request.MessageId} from source {request.SourceChannelId}.");
        }




        [HttpGet("rules")]
        public async Task<ActionResult<IEnumerable<ForwardingRule>>> GetAllRules(CancellationToken cancellationToken)
        {
            try
            {
                IEnumerable<ForwardingRule> rules = await _forwardingService.GetAllRulesAsync(cancellationToken);
                return Ok(rules);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.GetAllRules: Error retrieving forwarding rules.");
                return StatusCode(500, "Error retrieving forwarding rules.");
            }
        }




        [HttpGet("rules/{ruleName}")]
        public async Task<ActionResult<ForwardingRule>> GetRule(string ruleName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                return BadRequest("Rule name cannot be empty.");
            }
            try
            {
                ForwardingRule? rule = await _forwardingService.GetRuleAsync(ruleName, cancellationToken);
                if (rule == null)
                {
                    _logger.LogWarning("CONTROLLER.GetRule: Rule '{RuleName}' not found.", ruleName);
                    return NotFound($"Rule '{ruleName}' not found.");
                }
                return Ok(rule);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.GetRule: Error retrieving forwarding rule {RuleName}.", ruleName);
                return StatusCode(500, "Error retrieving forwarding rule.");
            }
        }



        [HttpGet("rules/channel/{sourceChannelId}")]
        public async Task<ActionResult<IEnumerable<ForwardingRule>>> GetRulesBySourceChannel(long sourceChannelId, CancellationToken cancellationToken)
        {
            if (sourceChannelId == 0)
            {
                return BadRequest("Source channel ID cannot be zero.");
            }
            try
            {
                IEnumerable<ForwardingRule> rules = await _forwardingService.GetRulesBySourceChannelAsync(sourceChannelId, cancellationToken);
                return Ok(rules);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.GetRulesBySourceChannel: Error retrieving forwarding rules for channel {ChannelId}.", sourceChannelId);
                return StatusCode(500, "Error retrieving forwarding rules.");
            }
        }




        [HttpPost("rules")]
        public async Task<ActionResult> CreateRule([FromBody] ForwardingRule rule, CancellationToken cancellationToken)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.RuleName))
            {
                return BadRequest("Invalid rule data or rule name missing.");
            }
            try
            {
                await _forwardingService.CreateRuleAsync(rule, cancellationToken);
                _logger.LogInformation("CONTROLLER.CreateRule: Rule '{RuleName}' created successfully.", rule.RuleName);
                return CreatedAtAction(nameof(GetRule), new { ruleName = rule.RuleName }, rule);
            }
            catch (InvalidOperationException opEx)
            {
                _logger.LogWarning(opEx, "CONTROLLER.CreateRule: Error creating forwarding rule {RuleName}.", rule.RuleName);
                return Conflict(opEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.CreateRule: General error creating forwarding rule {RuleName}.", rule.RuleName);
                return StatusCode(500, "Error creating forwarding rule.");
            }
        }



        [HttpPut("rules/{ruleName}")]
        public async Task<ActionResult> UpdateRule(string ruleName, [FromBody] ForwardingRule rule, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ruleName) || rule == null || ruleName != rule.RuleName)
            {
                return BadRequest("Rule name mismatch or invalid rule data.");
            }

            try
            {
                await _forwardingService.UpdateRuleAsync(rule, cancellationToken);
                var sanitizedRuleName = rule.RuleName.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "");
                _logger.LogInformation("CONTROLLER.UpdateRule: Rule '{RuleName}' updated successfully.", sanitizedRuleName);
                return Ok();
            }
            catch (InvalidOperationException opEx)
            {
                _logger.LogWarning(opEx, "CONTROLLER.UpdateRule: Error updating forwarding rule {RuleName}.", rule.RuleName);
                return NotFound(opEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.UpdateRule: General error updating forwarding rule {RuleName}.", ruleName);
                return StatusCode(500, "Error updating forwarding rule.");
            }
        }



        [HttpDelete("rules/{ruleName}")]
        public async Task<ActionResult> DeleteRule(string ruleName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                return BadRequest("Rule name cannot be empty.");
            }
            try
            {
                await _forwardingService.DeleteRuleAsync(ruleName, cancellationToken);
                var sanitizedRuleName = ruleName.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "");
                _logger.LogInformation("CONTROLLER.DeleteRule: Rule '{RuleName}' deleted successfully.", sanitizedRuleName);
                return Ok();
            }
            catch (InvalidOperationException opEx)
            {
                _logger.LogWarning(opEx, "CONTROLLER.DeleteRule: Error deleting forwarding rule {RuleName}.", ruleName);
                return NotFound(opEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.DeleteRule: General error deleting forwarding rule {RuleName}.", ruleName);
                return StatusCode(500, "Error deleting forwarding rule.");
            }
        }



    }
}