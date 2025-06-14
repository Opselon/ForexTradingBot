// File: WebAPI/Controllers/RssAdminController.cs
#region Usings
using Application.Features.Rss.Queries; // For FetchRssSourceManualQuery
using MediatR;                          // For ISender (or IMediator)
using Microsoft.AspNetCore.Mvc;         // For ApiController, Route, HttpPost, IActionResult, ProblemDetails
using System.Diagnostics;               // For Stopwatch (Level 7: Performance Logging)
#endregion

namespace WebAPI.Controllers
{
    /// <summary>
    /// Controller for administrative actions related to RSS feeds.
    /// This controller provides endpoints to manually trigger RSS feed fetching operations.
    /// </summary>
    /// <remarks>
    /// In a production environment, this controller should be protected by robust
    /// authentication and authorization mechanisms (e.g., `[Authorize(Roles = AppRoles.Administrator)]`).
    /// API versioning should also be considered (e.g., `/api/v1/admin/rss`).
    /// </remarks>
    [ApiController]
    [Route("api/admin/rss")] // Level 8: Consider versioning (e.g., "api/v1/admin/rss") for scalability.
    // [Authorize(Roles = "Administrator")] // Level 6: Security - uncomment and configure for production.
    public class RssAdminController : ControllerBase
    {
        private readonly ISender _mediator;
        private readonly ILogger<RssAdminController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RssAdminController"/> class.
        /// </summary>
        /// <param name="mediator">The MediatR sender instance for dispatching queries/commands.</param>
        /// <param name="logger">The logger instance for logging messages.</param>
        public RssAdminController(ISender mediator, ILogger<RssAdminController> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Manually triggers the fetching and processing of RSS feeds.
        /// </summary>
        /// <param name="rssSourceId">(Optional) The ID of a specific RssSource to fetch. If not provided, all active sources will be processed.</param>
        /// <param name="forceFetch">(Optional) If true, fetches the feed unconditionally, ignoring ETag/LastModified headers and existing items cache.</param>
        /// <param name="cancellationToken">Cancellation token to observe for client disconnection or server shutdown.</param>
        /// <returns>
        /// An <see cref="IActionResult"/> representing the outcome of the fetch operation.
        /// Returns 200 OK on success, 400 Bad Request for validation or client errors,
        /// 404 Not Found if a specific RSS source ID is provided but not found,
        /// and 500 Internal Server Error for unhandled exceptions.
        /// </returns>
        [HttpPost("fetch")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))] // Level 3: Specify return type for error responses.
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))] // Level 3: Specify return type for error responses.
        [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))] // Level 3: Specify return type for error responses.
        public async Task<IActionResult> FetchRssFeeds(
            [FromQuery] Guid? rssSourceId,
            [FromQuery] bool forceFetch = false,
            CancellationToken cancellationToken = default)
        {
            // Level 2: Define and begin logging scope for request correlation.
            string requestCorrelationId = HttpContext.TraceIdentifier; // Or Activity.Current?.Id;
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["RequestCorrelationId"] = requestCorrelationId,
                ["Endpoint"] = nameof(FetchRssFeeds)
            }))
            {
                _logger.LogInformation("API Request: Attempting to fetch RSS feeds. RssSourceId: {RssSourceId}, ForceFetch: {ForceFetch}. CorrelationId: {CorrelationId}",
                    rssSourceId?.ToString(), forceFetch, requestCorrelationId); // Level 2: Guid.ToString() for logging.

                Stopwatch stopwatch = Stopwatch.StartNew(); // Level 7: Start performance timer.

                try
                {
                    FetchRssSourceManualQuery query = new()
                    {
                        RssSourceId = rssSourceId,
                        ForceFetch = forceFetch
                    };

                    // Level 5: Proper CancellationToken propagation and ConfigureAwait(false).
                    Shared.Results.Result<IEnumerable<Application.DTOs.News.NewsItemDto>> result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);

                    stopwatch.Stop(); // Level 7: Stop timer.

                    if (result.Succeeded)
                    {
                        // Level 2: Success logging with new item count.
                        _logger.LogInformation("API Success: RSS feeds processed successfully. NewItemsCount: {NewItemsCount}. Duration: {DurationMs}ms. CorrelationId: {CorrelationId}",
                            result.Data?.Count() ?? 0, stopwatch.ElapsedMilliseconds, requestCorrelationId);

                        // Level 3: Standardized success response body.
                        return Ok(new
                        {
                            Message = result.SuccessMessage ?? "RSS feeds processed successfully.",
                            NewItemsCount = result.Data?.Count() ?? 0,
                            FetchedItems = result.Data
                        });
                    }
                    else
                    {
                        // Level 3: Granular error handling based on result.Errors content.
                        // Consider implementing ProblemDetails (RFC 7807) for richer error responses.
                        ProblemDetails problemDetails = new()
                        {
                            Instance = HttpContext.Request.Path,
                            Title = "Error processing RSS feeds",
                            Status = StatusCodes.Status400BadRequest, // Default to BadRequest
                            Detail = result.FailureMessage ?? "One or more errors occurred."
                        };

                        if (result.Errors != null && result.Errors.Any())
                        {
                            // Level 3: Custom logic for NotFound.
                            // Assuming an "not found" error message implies the specific RssSourceId was invalid.
                            if (result.Errors.Any(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase) || e.Contains("invalid RSS source id", StringComparison.OrdinalIgnoreCase)))
                            {
                                problemDetails.Status = StatusCodes.Status404NotFound;
                                problemDetails.Title = "RSS Source Not Found";
                                _logger.LogWarning("API Failure: RSS source not found for ID: {RssSourceId}. Errors: {Errors}. CorrelationId: {CorrelationId}",
                                    rssSourceId?.ToString(), string.Join(";", result.Errors), requestCorrelationId); // Level 2: Guid.ToString()
                                return NotFound(problemDetails); // Level 3: Return NotFound.
                            }
                        }

                        // Level 2: General warning for failure.
                        _logger.LogWarning("API Failure: Failed to process RSS feeds. Errors: {Errors}. Duration: {DurationMs}ms. CorrelationId: {CorrelationId}",
                            string.Join("; ", result.Errors), stopwatch.ElapsedMilliseconds, requestCorrelationId);

                        return BadRequest(problemDetails); // Level 3: Return BadRequest.
                    }
                }
                catch (OperationCanceledException) // Level 3: Handle explicit cancellation.
                {
                    stopwatch.Stop(); // Stop timer.
                    _logger.LogInformation("API Request Cancelled: RSS feed fetching operation was cancelled by client. Duration: {DurationMs}ms. CorrelationId: {CorrelationId}",
                                           stopwatch.ElapsedMilliseconds, requestCorrelationId);
                    return StatusCode(StatusCodes.Status499ClientClosedRequest); // Standard for client closing request early
                }
                catch (Exception ex)
                {
                    stopwatch.Stop(); // Stop timer.
                    // Level 2: Catch-all error logging.
                    _logger.LogError(ex, "API Error: An unexpected error occurred while fetching RSS feeds. RssSourceId: {RssSourceId}, ForceFetch: {ForceFetch}. Duration: {DurationMs}ms. CorrelationId: {CorrelationId}",
                        rssSourceId?.ToString(), forceFetch, stopwatch.ElapsedMilliseconds, requestCorrelationId); // Level 2: Guid.ToString()

                    // Level 3: Standardized internal server error response.
                    ProblemDetails problemDetails = new()
                    {
                        Status = StatusCodes.Status500InternalServerError,
                        Title = "Internal Server Error",
                        Detail = "An unexpected error occurred. Please try again later.",
                        Instance = HttpContext.Request.Path
                    };
                    return StatusCode(StatusCodes.Status500InternalServerError, problemDetails);
                }
            }
        }
    }
}