using ForexTradingBot.Cli.Secrets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers;

/// <summary>
/// Admin API over the local secret vault. Values are NEVER included in list
/// responses; revealing a value is a separate, explicitly-requested, audit-logged
/// action. The vault itself lives on this machine only.
/// </summary>
[ApiController]
[Route("api/secrets")]
[Authorize(Roles = "Admin")]
public sealed class SecretsController : ControllerBase
{
    private readonly ISecretVault _vault;
    private readonly ILogger<SecretsController> _logger;

    public SecretsController(ISecretVault vault, ILogger<SecretsController> logger)
    {
        _vault = vault;
        _logger = logger;
    }

    /// <summary>Lists stored secret keys with metadata. Values are always redacted.</summary>
    [HttpGet]
    public IActionResult List()
    {
        var items = _vault.List().Select(r => new
        {
            key = r.Key,
            category = r.Category.ToString(),
            description = r.Description,
            updatedUtc = r.UpdatedUtc,
            value = SqliteSecretVault.RedactedPlaceholder,
        });
        return Ok(new { items });
    }

    /// <summary>Returns a single secret's plaintext. Audit-logged, never cached.</summary>
    [HttpGet("{key}/reveal")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Reveal(string key)
    {
        var value = _vault.Get(key);
        if (value is null)
        {
            return NotFound(new { error = $"No secret named '{key}' exists." });
        }

        _logger.LogInformation("Secret '{Key}' revealed by {User} from {IP}", key, User.Identity?.Name ?? "unknown", HttpContext.Connection.RemoteIpAddress);
        return Ok(new { key, value });
    }

    [HttpPut("{key}")]
    public IActionResult Set(string key, [FromBody] SetSecretRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            return BadRequest(new { error = "Value must not be empty." });
        }

        _vault.Set(key, request.Value, request.Category, request.Description);
        _logger.LogInformation("Secret '{Key}' stored/updated by {User}", key, User.Identity?.Name ?? "unknown");
        return NoContent();
    }

    [HttpDelete("{key}")]
    public IActionResult Delete(string key)
    {
        var removed = _vault.Delete(key);
        _logger.LogInformation("Secret '{Key}' deleted by {User}", key, User.Identity?.Name ?? "unknown");
        return removed ? NoContent() : NotFound(new { error = $"No secret named '{key}' exists." });
    }

    /// <summary>Re-encrypts every secret under a fresh salt. Does not change values.</summary>
    [HttpPost("rotate")]
    public IActionResult Rotate()
    {
        _vault.Rotate();
        _logger.LogInformation("Secret vault rotated by {User}", User.Identity?.Name ?? "unknown");
        return Ok(new { rotated = _vault.List().Count });
    }

    public sealed record SetSecretRequest(string Value, SecretCategory Category = SecretCategory.Other, string? Description = null);
}
