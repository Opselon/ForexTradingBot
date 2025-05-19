// File: Application/Features/Rss/Queries/FetchRssSourceManualQuery.cs
#region Usings
using MediatR; // برای IRequest
using Shared.Results; // برای Result
using System;
using System.Collections.Generic; // برای IEnumerable
using Application.DTOs.News; // برای NewsItemDto
#endregion

namespace Application.Features.Rss.Queries
{
    /// <summary>
    /// Query to manually trigger the fetching and processing of a specific RSS source (or all active ones).
    /// This is primarily for testing or manual intervention.
    /// </summary>
    public class FetchRssSourceManualQuery : IRequest<Result<IEnumerable<NewsItemDto>>>
    {
        /// <summary>
        /// (Optional) The ID of a specific RssSource to fetch.
        /// If null, all active RssSources might be fetched (behavior defined in handler).
        /// </summary>
        public Guid? RssSourceId { get; set; }

        /// <summary>
        /// (Optional) If true, forces fetching even if conditional GET headers suggest no change.
        /// Useful for testing or re-syncing.
        /// </summary>
        public bool ForceFetch { get; set; } = false;
    }
}