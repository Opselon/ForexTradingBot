// File: Infrastructure/Persistence/Repositories/RssSourceRepository.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces; // For IRssSourceRepository and IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore; // For EF Core specific methods like FirstOrDefaultAsync, ToListAsync, AnyAsync, AsNoTracking
using Microsoft.Extensions.Logging;             // For RssSource entity
using System;
using System.Collections.Generic;
using System.Linq; // For Where, OrderBy etc. on IQueryable
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// Implements IRssSourceRepository providing data access methods for RssSource entities
    /// using Entity Framework Core. Focuses on performance, robustness, and maintainability.
    /// </summary>
    public class RssSourceRepository : IRssSourceRepository
    {
        private readonly IAppDbContext _context;
        private static ILogger<RssSourceRepository> _logger = null!;

        // Constants for URL normalization (simplified for example, real normalization is complex)
        // Consider moving these to a dedicated UrlNormalizationService or utility class.
        private static readonly string[] UrlPrefixesToRemove = { "http://www.", "https://www.", "http://", "https://" };
        private const char UrlPathSeparator = '/';

        public RssSourceRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        #region Read Operations

        /// <inheritdoc />
        public async Task<RssSource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            // Performance: Direct primary key lookup.
            // AsNoTracking could be used if the entity is read-only in this context, but often
            // entities fetched by ID are intended for update. Decided by caller or specific use case.
            // For generic GetById, keeping tracking enabled is safer.
            _logger.LogTrace("RssSourceRepository: Fetching RssSource by ID: {Id}", id);
            return await _context.RssSources
                .FirstOrDefaultAsync(rs => rs.Id == id, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<RssSource?> GetByUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("RssSourceRepository: GetByUrlAsync called with null or empty URL.");
                return null;
            }

            // Robustness & Performance: Normalize URL before querying.
            // Storing a normalized version of the URL in the database is the most performant approach
            // as it allows for indexed lookups on the normalized string.
            // The normalization logic here is a simplified example.
            string normalizedUrl = NormalizeUrlForComparison(url);
            _logger.LogTrace("RssSourceRepository: Fetching RssSource by Normalized URL: {NormalizedUrl} (Original: {OriginalUrl})", normalizedUrl, url);

            // Assuming RssSource.Url stores the non-normalized or consistently normalized URL.
            // If RssSource.Url is not normalized, this query will effectively compare against potentially various formats.
            // The ideal solution is to have a RssSource.NormalizedUrl field that is queried against.
            // For this example, we assume we're comparing against the potentially non-normalized URL field using a normalized input.
            return await _context.RssSources
                .FirstOrDefaultAsync(rs => rs.Url == normalizedUrl || rs.Url == url.Trim(), cancellationToken); // Check both if DB doesn't store normalized
                                                                                                                // OR preferably, ensure rs.Url is ALREADY normalized in DB and query on that:
                                                                                                                // .FirstOrDefaultAsync(rs => rs.NormalizedUrl == normalizedUrl, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RssSource>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            // Performance: Use AsNoTracking() as this data is likely for display and not modification.
            // This reduces EF Core's change tracking overhead.
            _logger.LogTrace("RssSourceRepository: Fetching all RssSources, ordered by SourceName, AsNoTracking.");
            return await _context.RssSources
                .AsNoTracking() // Optimization for read-only scenarios
                .OrderBy(rs => rs.SourceName)
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RssSource>> GetActiveSourcesAsync(CancellationToken cancellationToken = default)
        {
            // Performance: Filtering by IsActive and ordering. AsNoTracking() is beneficial here too.

            return await _context.RssSources
                .Where(rs => rs.IsActive)
                .AsNoTracking() // Optimization for read-only scenarios
                .OrderBy(rs => rs.SourceName) // Consider if ordering is always needed or can be client-side/optional
                .ToListAsync(cancellationToken);
        }

        #endregion

        #region Write Operations

        /// <inheritdoc />
        public async Task AddAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            // Robustness: Parameter validation.
            if (rssSource == null)
            {
                _logger.LogError("RssSourceRepository: Attempted to add a null RssSource object.");
                throw new ArgumentNullException(nameof(rssSource));
            }

            // Robustness: Data sanitization/normalization before saving.
            rssSource.Url = SanitizeAndNormalizeUrl(rssSource.Url); // Normalize and trim URL
            rssSource.SourceName = SanitizeString(rssSource.SourceName);   // Trim and potentially sanitize name

            // Robustness: Set creation timestamp.
            // Assuming these fields exist on RssSource and are managed by the application.
            // If using database-generated dates, this might not be needed here.
            var now = DateTime.UtcNow;
            rssSource.CreatedAt = now; // Assuming a CreatedAt property exists
            rssSource.UpdatedAt = now; // Assuming an UpdatedAt property exists

            _logger.LogInformation("RssSourceRepository: Adding new RssSource. Name: {SourceName}, URL: {Url}", rssSource.SourceName, rssSource.Url);
            await _context.RssSources.AddAsync(rssSource, cancellationToken);
            // Note: SaveChangesAsync is expected to be called by a Unit of Work or service layer.
        }

        /// <inheritdoc />
        public Task UpdateAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            // Robustness: Parameter validation.
            if (rssSource == null)
            {
                _logger.LogError("RssSourceRepository: Attempted to update with a null RssSource object.");
                throw new ArgumentNullException(nameof(rssSource));
            }

            // Robustness: Data sanitization/normalization.
            rssSource.Url = SanitizeAndNormalizeUrl(rssSource.Url);
            rssSource.SourceName = SanitizeString(rssSource.SourceName);

            // Robustness: Update the 'UpdatedAt' timestamp.
            rssSource.UpdatedAt = DateTime.UtcNow; // Assuming an UpdatedAt property exists

            _logger.LogInformation("RssSourceRepository: Marking RssSource for update. ID: {Id}, Name: {SourceName}, URL: {Url}", rssSource.Id, rssSource.SourceName, rssSource.Url);
            // EF Core tracks changes on entities fetched from the context.
            // If rssSource was fetched by this context, changes are tracked.
            // Explicitly setting state is useful if entity was created outside or attached from detached state.
            var entry = _context.RssSources.Entry(rssSource);
            if (entry.State == EntityState.Detached)
            {
                // If the entity is detached (e.g., came from a web request without being tracked),
                // we need to attach it and set its state to Modified.
                // However, a common pattern is to first fetch the entity, update its properties,
                // and then SaveChanges will persist those. This avoids updating all fields
                // if only some changed, and works better with concurrency tokens.
                // The `_context.RssSources.Update(rssSource);` method can also be used, which marks all properties as modified.
                // For this example, respecting original explicit state setting:
                _context.RssSources.Attach(rssSource); // Ensure it's tracked
            }
            entry.State = EntityState.Modified;

            return Task.CompletedTask;
            // Note: SaveChangesAsync is expected to be called by a Unit of Work or service layer.
        }

        /// <inheritdoc />
        public Task DeleteAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            // Robustness: Parameter validation.
            if (rssSource == null)
            {
                _logger.LogError("RssSourceRepository: Attempted to delete a null RssSource object.");
                throw new ArgumentNullException(nameof(rssSource));
            }

            _logger.LogInformation("RssSourceRepository: Marking RssSource for deletion. ID: {Id}, Name: {SourceName}", rssSource.Id, rssSource.SourceName);
            _context.RssSources.Remove(rssSource);
            // Using Task.CompletedTask as the operation is synchronous regarding EF Core's change tracking.
            // The actual DB operation happens on SaveChangesAsync.
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("RssSourceRepository: Attempting to delete RssSource by ID: {Id}", id);
            // Performance: Fetches entity first to ensure it exists and to allow for any pre-delete logic if needed.
            var sourceToDelete = await GetByIdAsync(id, cancellationToken); // Uses the already defined GetByIdAsync
            if (sourceToDelete == null)
            {
                _logger.LogWarning("RssSourceRepository: RssSource with ID {Id} not found for deletion.", id);
                return false; // Not found, nothing to delete.
            }

            // Calls the other DeleteAsync to mark for removal.
            await DeleteAsync(sourceToDelete, cancellationToken); // This returns Task.CompletedTask
            return true; // Marked for deletion, pending SaveChangesAsync.
        }

        #endregion

        #region Existence Checks

        /// <inheritdoc />
        public async Task<bool> ExistsByUrlAsync(string url, Guid? excludeId = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("RssSourceRepository: ExistsByUrlAsync called with null or empty URL.");
                return false; // Or throw, depending on expected behavior for invalid input.
            }

            string normalizedUrl = NormalizeUrlForComparison(url);
            _logger.LogTrace("RssSourceRepository: Checking existence by Normalized URL: {NormalizedUrl} (Original: {OriginalUrl}), ExcludeID: {ExcludeId}", normalizedUrl, url, excludeId);

            var query = _context.RssSources
                .Where(rs => rs.Url == normalizedUrl || rs.Url == url.Trim()); // Query against non-normalized if DB is not normalized
                                                                               // Or, preferably: .Where(rs => rs.NormalizedUrl == normalizedUrl);

            if (excludeId.HasValue)
            {
                query = query.Where(rs => rs.Id != excludeId.Value);
            }

            // Performance: AnyAsync is efficient as it translates to `EXISTS` in SQL.
            return await query.AnyAsync(cancellationToken);
        }

        #endregion

        #region Helper Methods for Sanitization/Normalization (Private)

        /// <summary>
        /// Normalizes a URL for comparison or storage.
        /// This is a SIMPLIFIED example. Robust URL normalization is complex and should ideally use established libraries.
        /// E.g., convert to lowercase, remove default ports, ensure consistent trailing slash, handle www.
        /// </summary>
        /// <param name="url">The URL string to normalize.</param>
        /// <returns>A normalized version of the URL.</returns>
        private string NormalizeUrlForComparison(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            var uriBuilder = new UriBuilder(url.Trim()); // Handles basic parsing

            // Convert scheme and host to lowercase (standard for normalization)
            uriBuilder.Scheme = uriBuilder.Scheme.ToLowerInvariant();
            uriBuilder.Host = uriBuilder.Host.ToLowerInvariant();

            // Remove common prefixes from host for consistency if "www." is to be ignored.
            // Example: remove "www." if you want "example.com" and "www.example.com" to match.
            // This specific logic depends on your business rules.
            if (uriBuilder.Host.StartsWith("www."))
            {
                uriBuilder.Host = uriBuilder.Host.Substring(4);
            }

            // Ensure path ends with a slash if it's only a domain, or normalize path segment cases if needed.
            string path = uriBuilder.Path.TrimEnd(UrlPathSeparator);
            if (string.IsNullOrEmpty(path) || path == "/") // Domain only or root
            {
                uriBuilder.Path = "/"; // Consistent trailing slash for root
            }
            else
            {
                uriBuilder.Path = path; // Path without trailing slash for non-root
            }

            // Remove default port (80 for http, 443 for https)
            if ((uriBuilder.Scheme == "http" && uriBuilder.Port == 80) ||
                (uriBuilder.Scheme == "https" && uriBuilder.Port == 443))
            {
                uriBuilder.Port = -1; // UriBuilder will omit port if -1
            }

            // Query string parameter order can also be part of normalization, but is complex. Omitted here.

            return uriBuilder.Uri.AbsoluteUri.TrimEnd(UrlPathSeparator); // Return consistent string representation, remove final trailing slash.
        }


        /// <summary>
        /// Creates a normalized version of the URL primarily intended for consistent storage.
        /// This might be slightly different from `NormalizeUrlForComparison` if comparison needs to be more lenient.
        /// </summary>
        private string SanitizeAndNormalizeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("RssSourceRepository: URL provided for sanitization was null or empty. Returning empty string.");
                // Or throw ArgumentException("URL cannot be empty.");
                return string.Empty;
            }
            // Robustness: Trim leading/trailing whitespace.
            string trimmedUrl = url.Trim();

            // Perform robust normalization. The one from comparison can be used or a stricter one for storage.
            // Example: Ensure it starts with http/https or add a default scheme if missing.
            // UriBuilder will throw if scheme is missing and not properly parsable.
            try
            {
                UriBuilder uriBuilder = new UriBuilder(trimmedUrl);
                if (string.IsNullOrWhiteSpace(uriBuilder.Scheme))
                {
                    // Default to https if no scheme provided.
                    uriBuilder.Scheme = "https";
                    uriBuilder.Port = -1; // Reset port if scheme changes.
                    _logger.LogInformation("RssSourceRepository: URL '{OriginalUrl}' had no scheme, defaulted to https. New URL: '{NewUrl}'", trimmedUrl, uriBuilder.Uri.AbsoluteUri);

                }
                // Further normalization like removing "www." or ensuring trailing slashes can be done here,
                // similar to NormalizeUrlForComparison.
                return NormalizeUrlForComparison(uriBuilder.Uri.AbsoluteUri); // Reuse comparison normalization for consistency.
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "RssSourceRepository: Invalid URL format for '{Url}'. Cannot sanitize/normalize. Returning original trimmed URL.", trimmedUrl);
                // Depending on strictness, either return trimmedUrl or throw an exception.
                // Returning original value might lead to inconsistent data. Throwing ensures valid URLs.
                // For this example, returning original trimmed if it can't be parsed to allow saving malformed ones for later review
                // but this should ideally be a validation error.
                // throw new ArgumentException($"URL '{trimmedUrl}' is invalid.", nameof(url), ex); // A stricter approach
                return trimmedUrl; // Lenient approach
            }
        }

        /// <summary>
        /// Basic sanitization for string fields like SourceName.
        /// Mainly trims whitespace. More complex sanitization (e.g., for XSS) is typically handled at input boundaries or UI layer.
        /// </summary>
        private string SanitizeString(string? inputString)
        {
            if (string.IsNullOrWhiteSpace(inputString))
            {
                // Consider if empty string is acceptable or should be an error / default value.
                _logger.LogTrace("RssSourceRepository: Input string for sanitization was null or empty. Returning empty string.");
                return string.Empty;
            }
            // Robustness: Trim whitespace. Max length check should ideally be done via validation attributes or FluentValidation.
            return inputString.Trim();
        }




        #endregion
    }
}