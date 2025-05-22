using Application.Features.Forwarding.Services;
using Domain.Features.Forwarding.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Models;

namespace WebAPI.Features.Forwarding.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize]
    public class ForwardingController : ControllerBase
    {
        private readonly IForwardingService _forwardingService;
        private readonly ILogger<ForwardingController> _logger;

        public ForwardingController(
            IForwardingService forwardingService,
            ILogger<ForwardingController> logger)
        {
            _forwardingService = forwardingService ?? throw new ArgumentNullException(nameof(forwardingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<ForwardingRule>>> GetAllRules(CancellationToken cancellationToken)
        {
            var rules = await _forwardingService.GetAllRulesAsync(cancellationToken);
            return Ok(rules);
        }

        [HttpGet("{ruleName}")]
        public async Task<ActionResult<ForwardingRule>> GetRule(string ruleName, CancellationToken cancellationToken)
        {
            var rule = await _forwardingService.GetRuleAsync(ruleName, cancellationToken);
            if (rule == null)
            {
                return NotFound();
            }
            return Ok(rule);
        }

        [HttpGet("channel/{sourceChannelId}")]
        public async Task<ActionResult<IEnumerable<ForwardingRule>>> GetRulesBySourceChannel(
            long sourceChannelId,
            CancellationToken cancellationToken)
        {
            var rules = await _forwardingService.GetRulesBySourceChannelAsync(sourceChannelId, cancellationToken);
            return Ok(rules);
        }

        [HttpPost]
        public async Task<ActionResult<ForwardingRule>> CreateRule(
            [FromBody] ForwardingRule rule,
            CancellationToken cancellationToken)
        {
            try
            {
                await _forwardingService.CreateRuleAsync(rule, cancellationToken);
                return CreatedAtAction(nameof(GetRule), new { ruleName = rule.RuleName }, rule);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpPut("{ruleName}")]
        public async Task<IActionResult> UpdateRule(
            string ruleName,
            [FromBody] ForwardingRule rule,
            CancellationToken cancellationToken)
        {
            if (ruleName != rule.RuleName)
            {
                return BadRequest("Rule name in URL must match rule name in body");
            }

            try
            {
                await _forwardingService.UpdateRuleAsync(rule, cancellationToken);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpDelete("{ruleName}")]
        public async Task<IActionResult> DeleteRule(string ruleName, CancellationToken cancellationToken)
        {
            try
            {
                await _forwardingService.DeleteRuleAsync(ruleName, cancellationToken);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpPost("process")]
        public async Task<IActionResult> ProcessMessage(
            [FromBody] ProcessMessageRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _forwardingService.ProcessMessageAsync(
                    request.SourceChannelId,
                    request.MessageId,
                    cancellationToken);
                return Accepted();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message");
                return StatusCode(500, "An error occurred while processing the message");
            }
        }
    }
} 