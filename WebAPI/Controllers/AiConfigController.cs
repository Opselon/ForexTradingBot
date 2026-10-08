using Application.Common.Interfaces;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace WebAPI.Controllers;

/// <summary>
/// CRUD over <see cref="AiApiConfiguration"/>: AI providers (Gemini, OpenAI-compatible),
/// their API keys, models and — most importantly — the analysis prompt template each
/// one runs. The panel's "AI Analysis Settings" screen talks to this controller.
/// </summary>
[ApiController]
[Route("api/ai/config")]
[Authorize]
public sealed class AiConfigController : ControllerBase
{
    // The Dapper repository only exposes enabled rows; the panel needs every row,
    // including disabled providers, so list endpoints go to the DbContext directly.
    private readonly AppDbContext _dbContext;
    private readonly IAiApiConfigurationRepository _repository;
    private readonly ILogger<AiConfigController> _logger;

    public AiConfigController(AppDbContext dbContext, IAiApiConfigurationRepository repository, ILogger<AiConfigController> logger)
    {
        _dbContext = dbContext;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Every configured AI provider, enabled or not.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<AiConfigDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AiConfigDto>>> GetAll(CancellationToken cancellationToken)
    {
        List<AiApiConfiguration> all = await _dbContext.AiApiConfigurations
            .AsNoTracking()
            .OrderByDescending(c => c.IsEnabled)
            .ThenBy(c => c.ProviderName)
            .ToListAsync(cancellationToken);

        return Ok(all.Select(Map));
    }

    /// <summary>One provider configuration by id.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(AiConfigDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiConfigDto>> GetById(int id, CancellationToken cancellationToken)
    {
        AiApiConfiguration? found = await _dbContext.AiApiConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        return found is null ? NotFound() : Ok(Map(found));
    }

    /// <summary>Creates a provider configuration with its key, model and prompt template.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(AiConfigDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiConfigDto>> Create(
        [FromBody] SaveAiConfigRequest request,
        CancellationToken cancellationToken)
    {
        string? problem = Validate(request);
        if (problem is not null)
        {
            return BadRequest(problem);
        }

        if (await _repository.ExistsAsync(request.ProviderName!, cancellationToken))
        {
            return BadRequest($"A configuration for provider '{request.ProviderName}' already exists. Update it instead.");
        }

        AiApiConfiguration entity = new()
        {
            ProviderName = request.ProviderName!,
            IsEnabled = request.IsEnabled,
            ApiKey = request.ApiKey!,
            ModelName = request.ModelName!,
            PromptTemplate = request.PromptTemplate ?? string.Empty,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow
        };

        _ = await _repository.AddAsync(entity, cancellationToken);
        _logger.LogInformation("Panel: created AI provider {Provider} (model {Model})", entity.ProviderName, entity.ModelName);

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, Map(entity));
    }

    /// <summary>Updates the key, model, prompt template or enabled flag of a provider.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] SaveAiConfigRequest request,
        CancellationToken cancellationToken)
    {
        string? problem = Validate(request);
        if (problem is not null)
        {
            return BadRequest(problem);
        }

        AiApiConfiguration? existing = await _dbContext.AiApiConfigurations
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (existing is null)
        {
            return NotFound($"AI configuration {id} not found.");
        }

        // An empty ApiKey in the request body means "keep the current key" — the panel
        // never has to round-trip a secret to change a prompt or a model.
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("ApiKey is required. Send the current key to confirm the change.");
        }

        existing.ProviderName = request.ProviderName!;
        existing.IsEnabled = request.IsEnabled;
        existing.ApiKey = request.ApiKey!;
        existing.ModelName = request.ModelName!;
        existing.PromptTemplate = request.PromptTemplate ?? string.Empty;
        existing.Description = request.Description;
        existing.LastUpdatedAt = DateTime.UtcNow;

        await _repository.UpdateAsync(existing, cancellationToken);
        _logger.LogInformation("Panel: updated AI provider {Provider} (id {Id})", existing.ProviderName, id);

        return NoContent();
    }

    /// <summary>Removes a provider configuration.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        AiApiConfiguration? existing = await _dbContext.AiApiConfigurations
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (existing is null)
        {
            return NotFound();
        }

        await _repository.DeleteAsync(id, cancellationToken);
        _logger.LogInformation("Panel: deleted AI configuration {Id}", id);
        return NoContent();
    }

    /// <summary>Turns a provider on or off without touching its key or prompt.</summary>
    [HttpPost("{id:int}/toggle")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Toggle(int id, [FromQuery] bool enabled, CancellationToken cancellationToken)
    {
        AiApiConfiguration? existing = await _dbContext.AiApiConfigurations
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (existing is null)
        {
            return NotFound();
        }

        existing.IsEnabled = enabled;
        existing.LastUpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);

        _logger.LogInformation("Panel: AI provider {Provider} is now {State}", existing.ProviderName, enabled ? "enabled" : "disabled");
        return NoContent();
    }

    private static string? Validate(SaveAiConfigRequest request)
    {
        if (request is null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.ProviderName))
        {
            return "ProviderName is required (e.g. Gemini).";
        }

        if (string.IsNullOrWhiteSpace(request.ModelName))
        {
            return "ModelName is required (e.g. gemini-2.0-flash).";
        }

        return null;
    }

    /// <summary>Never echoes the raw API key back to the browser.</summary>
    private static AiConfigDto Map(AiApiConfiguration c) => new()
    {
        Id = c.Id,
        ProviderName = c.ProviderName,
        IsEnabled = c.IsEnabled,
        // Mask the key: enough to identify which key it is, never enough to use it.
        ApiKeyMasked = Mask(c.ApiKey),
        HasKey = !string.IsNullOrWhiteSpace(c.ApiKey),
        ModelName = c.ModelName,
        PromptTemplate = c.PromptTemplate,
        Description = c.Description,
        CreatedAt = c.CreatedAt,
        LastUpdatedAt = c.LastUpdatedAt
    };

    private static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return key.Length <= 8 ? new string('•', key.Length) : $"{key[..4]}••••••{key[^4..]}";
    }
}

public sealed class AiConfigDto
{
    public int Id { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public string ApiKeyMasked { get; init; } = string.Empty;
    public bool HasKey { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string PromptTemplate { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastUpdatedAt { get; init; }
}

public sealed class SaveAiConfigRequest
{
    public string? ProviderName { get; init; }
    public bool IsEnabled { get; init; }
    /// <summary>Required on update. On create it is the key to store.</summary>
    public string? ApiKey { get; init; }
    public string? ModelName { get; init; }
    public string? PromptTemplate { get; init; }
    public string? Description { get; init; }
}
