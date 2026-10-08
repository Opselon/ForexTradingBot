using Application.Common.Interfaces;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers;

/// <summary>
/// CRUD over <see cref="RssSource"/> plus a manual fetch trigger. The panel's
/// "RSS Sites" screen lists, adds, edits and removes feeds here; the fetch
/// endpoint delegates to the same MediatR query the background job uses.
/// </summary>
[ApiController]
[Route("api/rss/sources")]
[Authorize]
public sealed class RssSourcesController : ControllerBase
{
    private readonly IRssSourceRepository _repository;
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<RssSourcesController> _logger;

    public RssSourcesController(IRssSourceRepository repository, IAppDbContext dbContext, ILogger<RssSourcesController> logger)
    {
        _repository = repository;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>Every RSS source with its fetch health, so the panel can show
    /// which feeds are failing, not just which exist.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RssSourceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RssSourceDto>>> GetAll(CancellationToken cancellationToken)
    {
        IEnumerable<RssSource> all = await _repository.GetAllAsync(cancellationToken);
        return Ok(all.OrderByDescending(s => s.IsActive).ThenBy(s => s.SourceName).Select(Map));
    }

    /// <summary>Only the sources the scheduler is currently collecting from.</summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(IEnumerable<RssSourceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RssSourceDto>>> GetActive(CancellationToken cancellationToken)
    {
        IEnumerable<RssSource> active = await _repository.GetActiveSourcesAsync(cancellationToken);
        return Ok(active.Select(Map));
    }

    /// <summary>One feed by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RssSourceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RssSourceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        RssSource? found = await _repository.GetByIdAsync(id, cancellationToken);
        return found is null ? NotFound() : Ok(Map(found));
    }

    /// <summary>Adds a new feed. Duplicate URLs are rejected before they hit the DB.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RssSourceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RssSourceDto>> Create(
        [FromBody] SaveRssSourceRequest request,
        CancellationToken cancellationToken)
    {
        string? problem = Validate(request);
        if (problem is not null)
        {
            return BadRequest(problem);
        }

        if (await _repository.ExistsByUrlAsync(request.Url!, cancellationToken: cancellationToken))
        {
            return BadRequest("A source with this URL already exists.");
        }

        RssSource entity = new()
        {
            Id = Guid.NewGuid(),
            Url = request.Url!,
            SourceName = request.SourceName!,
            IsActive = request.IsActive,
            Description = request.Description,
            FetchIntervalMinutes = request.FetchIntervalMinutes,
            DefaultSignalCategoryId = request.DefaultSignalCategoryId,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Panel: added RSS source {Name} ({Url})", entity.SourceName, entity.Url);

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, Map(entity));
    }

    /// <summary>Edits a feed's URL, name, interval or active flag.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] SaveRssSourceRequest request,
        CancellationToken cancellationToken)
    {
        string? problem = Validate(request);
        if (problem is not null)
        {
            return BadRequest(problem);
        }

        RssSource? existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound($"RSS source {id} not found.");
        }

        if (await _repository.ExistsByUrlAsync(request.Url!, excludeId: id, cancellationToken: cancellationToken))
        {
            return BadRequest("Another source already uses this URL.");
        }

        existing.Url = request.Url!;
        existing.SourceName = request.SourceName!;
        existing.IsActive = request.IsActive;
        existing.Description = request.Description;
        existing.FetchIntervalMinutes = request.FetchIntervalMinutes;
        existing.DefaultSignalCategoryId = request.DefaultSignalCategoryId;
        existing.UpdatedAt = DateTime.UtcNow;

        await _repository.UpdateAsync(existing, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Panel: updated RSS source {Name}", existing.SourceName);

        return NoContent();
    }

    /// <summary>Removes a feed and stops the scheduler collecting from it.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        bool deleted = await _repository.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Panel: deleted RSS source {Id}", id);
        return NoContent();
    }

    /// <summary>Pauses or resumes a feed without deleting it.</summary>
    [HttpPost("{id:guid}/toggle")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Toggle(Guid id, [FromQuery] bool active, CancellationToken cancellationToken)
    {
        RssSource? existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        existing.IsActive = active;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Panel: RSS source {Name} is now {State}", existing.SourceName, active ? "active" : "paused");
        return NoContent();
    }

    private static string? Validate(SaveRssSourceRequest request)
    {
        if (request is null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.SourceName))
        {
            return "SourceName is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return "Url is required.";
        }

        // A feed URL that is not absolute can never be fetched; catch it here rather
        // than letting the fetcher fail on the next job run.
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return "Url must be an absolute URL including the scheme (https://…).";
        }

        if (request.FetchIntervalMinutes.HasValue && request.FetchIntervalMinutes.Value < 1)
        {
            return "FetchIntervalMinutes must be at least 1 minute.";
        }

        return null;
    }

    private static RssSourceDto Map(RssSource s) => new()
    {
        Id = s.Id,
        Url = s.Url,
        SourceName = s.SourceName,
        IsActive = s.IsActive,
        Description = s.Description,
        FetchIntervalMinutes = s.FetchIntervalMinutes,
        DefaultSignalCategoryId = s.DefaultSignalCategoryId,
        LastFetchAttemptAt = s.LastFetchAttemptAt,
        LastSuccessfulFetchAt = s.LastSuccessfulFetchAt,
        FetchErrorCount = s.FetchErrorCount,
        NewsItemCount = s.NewsItems?.Count ?? 0
    };
}

public sealed class RssSourceDto
{
    public Guid Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string? Description { get; init; }
    public int? FetchIntervalMinutes { get; init; }
    public Guid? DefaultSignalCategoryId { get; init; }
    public DateTime? LastFetchAttemptAt { get; init; }
    public DateTime? LastSuccessfulFetchAt { get; init; }
    public int FetchErrorCount { get; init; }
    public int NewsItemCount { get; init; }

    /// <summary>Derived health for the panel's status pill.</summary>
    public string Health => !IsActive
        ? "paused"
        : FetchErrorCount switch
        {
            0 => "ok",
            < 3 => "warn",
            _ => "error"
        };
}

public sealed class SaveRssSourceRequest
{
    public string? SourceName { get; init; }
    public string? Url { get; init; }
    public bool IsActive { get; init; } = true;
    public string? Description { get; init; }
    public int? FetchIntervalMinutes { get; init; }
    public Guid? DefaultSignalCategoryId { get; init; }
}
