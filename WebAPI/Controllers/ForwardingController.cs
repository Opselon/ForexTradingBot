using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Features.Forwarding.Services;
using Domain.Features.Forwarding.Entities;
using Hangfire;
using Infrastructure.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using WebAPI.Models;

namespace WebAPI.Controllers
{
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

        [HttpPost("process/background")]
        public IActionResult ProcessMessage([FromBody] ProcessMessageRequest request)
        {
            if (request == null)
            {
                return BadRequest("Invalid request");
            }

            BackgroundJob.Enqueue<ForwardingJob>(job => job.ProcessMessageAsync(request.SourceChannelId, request.MessageId, CancellationToken.None));
            return Ok("Message processing job enqueued");
        }

        [HttpGet("rules")]
        public async Task<ActionResult<IEnumerable<ForwardingRule>>> GetAllRules(CancellationToken cancellationToken)
        {
            try
            {
                var rules = await _forwardingService.GetAllRulesAsync(cancellationToken);
                return Ok(rules);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving forwarding rules");
                return StatusCode(500, "Error retrieving forwarding rules");
            }
        }

        [HttpGet("rules/{ruleName}")]
        public async Task<ActionResult<ForwardingRule>> GetRule(string ruleName, CancellationToken cancellationToken)
        {
            try
            {
                var rule = await _forwardingService.GetRuleAsync(ruleName, cancellationToken);
                if (rule == null)
                {
                    return NotFound($"Rule '{ruleName}' not found");
                }
                return Ok(rule);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving forwarding rule {RuleName}", ruleName);
                return StatusCode(500, "Error retrieving forwarding rule");
            }
        }

        [HttpGet("rules/channel/{sourceChannelId}")]
        public async Task<ActionResult<IEnumerable<ForwardingRule>>> GetRulesBySourceChannel(long sourceChannelId, CancellationToken cancellationToken)
        {
            try
            {
                var rules = await _forwardingService.GetRulesBySourceChannelAsync(sourceChannelId, cancellationToken);
                return Ok(rules);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving forwarding rules for channel {ChannelId}", sourceChannelId);
                return StatusCode(500, "Error retrieving forwarding rules");
            }
        }

        [HttpPost("rules")]
        public async Task<ActionResult> CreateRule([FromBody] ForwardingRule rule, CancellationToken cancellationToken)
        {
            try
            {
                await _forwardingService.CreateRuleAsync(rule, cancellationToken);
                return CreatedAtAction(nameof(GetRule), new { ruleName = rule.RuleName }, rule);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating forwarding rule {RuleName}", rule.RuleName);
                return StatusCode(500, "Error creating forwarding rule");
            }
        }

        [HttpPut("rules/{ruleName}")]
        public async Task<ActionResult> UpdateRule(string ruleName, [FromBody] ForwardingRule rule, CancellationToken cancellationToken)
        {
            if (ruleName != rule.RuleName)
            {
                return BadRequest("Rule name mismatch");
            }

            try
            {
                await _forwardingService.UpdateRuleAsync(rule, cancellationToken);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating forwarding rule {RuleName}", ruleName);
                return StatusCode(500, "Error updating forwarding rule");
            }
        }

        [HttpDelete("rules/{ruleName}")]
        public async Task<ActionResult> DeleteRule(string ruleName, CancellationToken cancellationToken)
        {
            try
            {
                await _forwardingService.DeleteRuleAsync(ruleName, cancellationToken);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting forwarding rule {RuleName}", ruleName);
                return StatusCode(500, "Error deleting forwarding rule");
            }
        }
    }
} 