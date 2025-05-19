// File: WebAPI/Controllers/RssAdminController.cs
#region Usings
using Application.Features.Rss.Queries; // برای FetchRssSourceManualQuery
using MediatR;                          // برای ISender (یا IMediator)
using Microsoft.AspNetCore.Http;        // برای StatusCodes
using Microsoft.AspNetCore.Mvc;         // برای ApiController, Route, HttpPost, IActionResult
using System;
using System.Threading.Tasks;
using System.Threading;
#endregion

namespace WebAPI.Controllers
{
    /// <summary>
    /// Controller for administrative actions related to RSS feeds.
    /// This controller should be protected by authorization in a real application.
    /// </summary>
    [ApiController]
    [Route("api/admin/rss")]
    // [Authorize(Roles = AppRoles.Administrator)] //  در آینده برای امنیت اضافه کنید
    public class RssAdminController : ControllerBase
    {
        private readonly ISender _mediator; //  استفاده از ISender (توصیه شده در MediatR جدید) یا IMediator

        public RssAdminController(ISender mediator)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        }

        /// <summary>
        /// Manually triggers the fetching and processing of RSS feeds.
        /// </summary>
        /// <param name="rssSourceId">(Optional) The ID of a specific RssSource to fetch. If not provided, all active sources will be processed.</param>
        /// <param name="forceFetch">(Optional) If true, fetches the feed unconditionally, ignoring ETag/LastModified headers.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A result indicating the outcome of the fetch operation.</returns>
        [HttpPost("fetch")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)] //  اگر RssSourceId مشخص شده و پیدا نشود
        public async Task<IActionResult> FetchRssFeeds(
            [FromQuery] Guid? rssSourceId,
            [FromQuery] bool forceFetch = false,
            CancellationToken cancellationToken = default)
        {
            var query = new FetchRssSourceManualQuery
            {
                RssSourceId = rssSourceId,
                ForceFetch = forceFetch
            };

            var result = await _mediator.Send(query, cancellationToken);

            if (result.Succeeded)
            {
                return Ok(new { Message = result.SuccessMessage ?? "RSS feeds processed successfully.", NewItemsCount = result.Data?.Count() ?? 0, FetchedItems = result.Data });
            }

            //  بررسی دقیق‌تر نوع خطا برای بازگرداندن کد وضعیت مناسب
            if (result.Errors.Any(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                return NotFound(new { Errors = result.Errors });
            }
            return BadRequest(new { Errors = result.Errors });
        }
    }
}