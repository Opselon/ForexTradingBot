using AutoMapper;
using Domain.Features.Forwarding.Entities; // For ForwardingRule entity used in GET/POST/PUT rules
using Hangfire;
using Infrastructure.Jobs; // For ForwardingJob
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Models;

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
        private readonly IMapper _mapper;
        public ForwardingController(IForwardingService forwardingService, ILogger<ForwardingController> logger, IMapper mapper)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
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
            // 1. Sanitize the input IMMEDIATELY.
            string sanitizedRuleName = ruleName?.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "") ?? string.Empty;

            // 2. Validate the SANITIZED input.
            if (string.IsNullOrWhiteSpace(sanitizedRuleName))
            {
                return BadRequest("Rule name cannot be empty.");
            }
            try
            {
                // 3. Use the SANITIZED input for the service call.
                ForwardingRule? rule = await _forwardingService.GetRuleAsync(sanitizedRuleName, cancellationToken);
                if (rule == null)
                {
                    // Logging is now safe.
                    _logger.LogWarning("CONTROLLER.GetRule: Rule '{RuleName}' not found.", sanitizedRuleName);
                    // Return the sanitized name to the client to prevent echoing malicious script content (minor XSS vector).
                    return NotFound($"Rule '{sanitizedRuleName}' not found.");
                }
                return Ok(rule);
            }
            catch (Exception ex)
            {
                // Logging is now safe.
                _logger.LogError(ex, "CONTROLLER.GetRule: Error retrieving forwarding rule {RuleName}.", sanitizedRuleName);
                return StatusCode(500, "Error retrieving forwarding rule.");
            }
        }


        [HttpGet("rules/channel/{sourceChannelId}")]
        // The return type is now the clean DTO, not the domain entity
        public async Task<ActionResult<IEnumerable<ForwardingRuleSummaryDto>>> GetRulesBySourceChannel(long sourceChannelId, CancellationToken cancellationToken)
        {
            // Input validation is good. No change needed here.
            if (sourceChannelId == 0)
            {
                return BadRequest("Source channel ID cannot be zero.");
            }
            try
            {
                IEnumerable<ForwardingRule> rules = await _forwardingService.GetRulesBySourceChannelAsync(sourceChannelId, cancellationToken);

                // SECURE: Map the internal domain entities to clean public-facing DTOs.
                // This prevents leaking the internal model structure.
                IEnumerable<ForwardingRuleSummaryDto> rulesDto = _mapper.Map<IEnumerable<ForwardingRuleSummaryDto>>(rules);

                return Ok(rulesDto);
            }
            catch (Exception ex)
            {
                // Logging is safe as sourceChannelId is a long.
                _logger.LogError(ex, "CONTROLLER.GetRulesBySourceChannel: Error retrieving forwarding rules for channel {ChannelId}.", sourceChannelId);

                // SECURE: Return a generic error message.
                return StatusCode(500, "An internal error occurred while retrieving forwarding rules.");
            }
        }





        [HttpPost("rules")]
        public async Task<ActionResult> CreateRule([FromBody] ForwardingRuleDto dto, CancellationToken cancellationToken)
        {
            if (dto == null)
            {
                return BadRequest("Invalid rule data.");
            }

            // Sanitize user input before it's used anywhere
            string sanitizedRuleName = dto.RuleName?.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sanitizedRuleName))
            {
                return BadRequest("Rule name is required.");
            }

            dto.RuleName = sanitizedRuleName;

            try
            {
                ForwardingRule newRule = _mapper.Map<ForwardingRule>(dto);

                await _forwardingService.CreateRuleAsync(newRule, cancellationToken);
                _logger.LogInformation("CONTROLLER.CreateRule: Rule '{RuleName}' created successfully.", sanitizedRuleName);
                return CreatedAtAction(nameof(GetRule), new { ruleName = sanitizedRuleName }, newRule);
            }
            catch (AutoMapperMappingException ex)
            {
                _logger.LogError(ex, "AutoMapper configuration error for CreateRule. Check MappingProfile against domain constructors.");
                return StatusCode(500, "An internal configuration error occurred.");
            }
            catch (InvalidOperationException opEx)
            {
                _logger.LogWarning(opEx, "CONTROLLER.CreateRule: Error creating rule {RuleName}.", sanitizedRuleName);
                return Conflict("A rule with this name may already exist.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.CreateRule: General error creating rule {RuleName}.", sanitizedRuleName);
                return StatusCode(500, "An internal error occurred.");
            }
        }

        [HttpPut("rules/{ruleName}")]
        public async Task<ActionResult> UpdateRule(string ruleName, [FromBody] ForwardingRuleDto dto, CancellationToken cancellationToken)
        {
            // Sanitize URL input immediately
            string sanitizedUrlRuleName = ruleName?.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sanitizedUrlRuleName) || dto == null)
            {
                return BadRequest("Rule name is invalid or request body is missing.");
            }

            // Sanitize DTO body input immediately
            string sanitizedDtoRuleName = dto.RuleName?.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "") ?? string.Empty;

            // Perform comparison with sanitized values
            if (sanitizedUrlRuleName != sanitizedDtoRuleName)
            {
                // Log is already safe because both inputs have been sanitized
                _logger.LogWarning("Potential parameter tampering in UpdateRule. URL: '{UrlRuleName}', Body (Sanitized): '{BodyRuleName}'.",
                                    sanitizedUrlRuleName,
                                    sanitizedDtoRuleName);
                return BadRequest("Rule name in URL must match the rule name in the request body.");
            }

            // Assign the sanitized name back to the DTO for mapping
            dto.RuleName = sanitizedDtoRuleName;

            try
            {
                ForwardingRule updatedRule = _mapper.Map<ForwardingRule>(dto);

                await _forwardingService.UpdateRuleAsync(updatedRule, cancellationToken);
                _logger.LogInformation("CONTROLLER.UpdateRule: Rule '{RuleName}' updated successfully.", sanitizedDtoRuleName);
                return NoContent();
            }
            catch (AutoMapperMappingException ex)
            {
                _logger.LogError(ex, "AutoMapper configuration error for UpdateRule. Check MappingProfile.");
                return StatusCode(500, "An internal configuration error occurred.");
            }
            catch (InvalidOperationException opEx)
            {
                // ==========================================================
                // VULNERABILITY REMEDIATION
                // ==========================================================
                // The vulnerable line is now fixed by using the sanitized variable.
                _logger.LogWarning(opEx, "CONTROLLER.UpdateRule: Error updating rule {RuleName}.", sanitizedDtoRuleName);
                // ==========================================================

                return NotFound("The specified rule could not be found or updated.");
            }
            catch (Exception ex)
            {
                // This log point is also now secure by using the sanitized variable.
                _logger.LogError(ex, "CONTROLLER.UpdateRule: General error updating rule {RuleName}.", sanitizedDtoRuleName);
                return StatusCode(500, "An internal error occurred.");
            }
        }


        [HttpDelete("rules/{ruleName}")]
        public async Task<ActionResult> DeleteRule(string ruleName, CancellationToken cancellationToken)
        {
            // 1. Sanitize the input IMMEDIATELY.
            string sanitizedRuleName = ruleName?.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "") ?? string.Empty;

            // 2. Validate the SANITIZED input.
            if (string.IsNullOrWhiteSpace(sanitizedRuleName))
            {
                return BadRequest("Rule name cannot be empty.");
            }

            try
            {
                // 3. SECURE: Pass ONLY the sanitized input to the service layer.
                await _forwardingService.DeleteRuleAsync(sanitizedRuleName, cancellationToken);

                // Logging is already safe with the sanitized variable.
                _logger.LogInformation("CONTROLLER.DeleteRule: Rule '{RuleName}' deleted successfully.", sanitizedRuleName);

                return NoContent(); // 204 NoContent is a more appropriate response for a successful DELETE.
            }
            catch (InvalidOperationException opEx)
            {
                _logger.LogWarning(opEx, "CONTROLLER.DeleteRule: Error deleting forwarding rule {RuleName}.", sanitizedRuleName);

                // 4. SECURE: Return a generic, safe error message instead of ex.Message.
                return NotFound("The specified rule could not be found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CONTROLLER.DeleteRule: General error deleting forwarding rule {RuleName}.", sanitizedRuleName);

                // SECURE: Return a generic error message.
                return StatusCode(500, "An internal error occurred while deleting the rule.");
            }
        }



    }
}