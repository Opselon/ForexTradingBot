using Application.DTOs.Settings;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers;

/// <summary>
/// The "Force Join" feature: users must be a member of a configured channel before
/// the bot responds to them. This controller exposes the setting to the panel so an
/// admin can turn it on, point it at a channel, and word the prompt the user sees —
/// without touching appsettings or restarting.
/// </summary>
[ApiController]
[Route("api/telegram/force-join")]
[Authorize]
public sealed class ForceJoinController : ControllerBase
{
    // Reads come from ISettingsService (cache-backed, fast); writes go through
    // IAdminService, which is the interface that actually declares the update method.
    private readonly ISettingsService _settingsService;
    private readonly IAdminService _adminService;
    private readonly ILogger<ForceJoinController> _logger;

    public ForceJoinController(ISettingsService settingsService, IAdminService adminService, ILogger<ForceJoinController> logger)
    {
        _settingsService = settingsService;
        _adminService = adminService;
        _logger = logger;
    }

    /// <summary>
    /// The current force-join configuration. Always returns 200 with a disabled
    /// default so the panel can render the form on a fresh install.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ForceJoinSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ForceJoinSettingsDto>> Get(CancellationToken cancellationToken)
    {
        // SettingsService reads through the Redis cache. When Redis is down that read
        // throws, but force-join is not important enough to take the panel with it —
        // fall back to a disabled default and say so.
        try
        {
            ForceJoinSettingsDto settings = await _settingsService.GetForceJoinSettingsAsync(cancellationToken);
            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Panel: force-join settings read failed; returning the disabled default.");
            return Ok(new ForceJoinSettingsDto { IsEnabled = false });
        }
    }

    /// <summary>
    /// Saves the configuration. The bot picks it up from the settings cache on the
    /// next message, so no restart is needed.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ForceJoinSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(
        [FromBody] ForceJoinSettingsDto request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        // The DTO has [Required] on ChannelId/ChannelLink/Message, but those are only
        // meaningful when the feature is on. Validate conditionally so an admin can
        // switch the feature off without filling in a channel.
        if (request.IsEnabled)
        {
            List<string> errors = [];

            if (request.ChannelId == 0)
            {
                errors.Add("ChannelId is required when Force Join is enabled (e.g. -1001234567890).");
            }

            if (string.IsNullOrWhiteSpace(request.ChannelLink))
            {
                errors.Add("ChannelLink is required when Force Join is enabled.");
            }
            else if (!Uri.TryCreate(request.ChannelLink, UriKind.Absolute, out _))
            {
                errors.Add("ChannelLink must be a valid URL (https://t.me/yourchannel).");
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                errors.Add("Message is required when Force Join is enabled.");
            }

            if (errors.Count > 0)
            {
                _logger.LogWarning("Panel: force-join save rejected — {Errors}", string.Join("; ", errors));
                return BadRequest(new ValidationProblemDetails(
                    new Dictionary<string, string[]> { ["settings"] = errors.ToArray() })
                {
                    Title = "Force Join validation failed",
                    Detail = string.Join(" ", errors)
                });
            }
        }

        await _adminService.UpdateForceJoinSettingsAsync(request, cancellationToken);

        _logger.LogInformation("Panel: force-join {State} (channel {ChannelId})",
            request.IsEnabled ? "enabled" : "disabled", request.ChannelId);

        return Ok(request);
    }

    /// <summary>
    /// Shortcut to toggle the feature without resending the whole object.
    /// </summary>
    [HttpPost("toggle")]
    [ProducesResponseType(typeof(ForceJoinSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Toggle([FromQuery] bool enabled, CancellationToken cancellationToken)
    {
        // Same Redis-down resilience as the GET above: fall back rather than 500.
        ForceJoinSettingsDto current;
        try
        {
            current = await _settingsService.GetForceJoinSettingsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Panel: force-join read failed before toggle; using the disabled default.");
            current = new ForceJoinSettingsDto { IsEnabled = false };
        }

        if (enabled && current.ChannelId == 0)
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["settings"] = ["Set the channel first, then enable Force Join."] })
            {
                Title = "Cannot enable Force Join",
                Detail = "Configure the channel ID, link and message before turning the feature on."
            });
        }

        current.IsEnabled = enabled;
        await _adminService.UpdateForceJoinSettingsAsync(current, cancellationToken);

        _logger.LogInformation("Panel: force-join toggled {State}", enabled ? "on" : "off");
        return Ok(current);
    }
}
