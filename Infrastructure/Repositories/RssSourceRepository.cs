// File: Infrastructure/Persistence/Repositories/RssSourceRepository.cs

#region Usings
using Application.Common.Interfaces;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly; // Added for Polly
using Polly.Retry; // Added for Polly
using System.Data.Common; // For DbException
#endregion

namespace Infrastructure.Repositories
{
    /// <summary>
    /// Implements IRssSourceRepository providing data access methods for RssSource entities
    /// using Entity Framework Core. Focuses on performance, robustness, and maintainability.
    /// Uses Polly for resilience against transient database failures.
    /// </summary>
    public class RssSourceRepository : IRssSourceRepository
    {
        private readonly IAppDbContext _context;
        private readonly ILogger<RssSourceRepository> _logger; // ✅ Changed to readonly field
        private readonly AsyncRetryPolicy _dbRetryPolicy; // ✅ Added for Polly

        // Constants for URL normalization (simplified for example, real normalization is complex)
        private static readonly string[] UrlPrefixsToRemove = { "http://www.", "https://www.", "http://", "https://" };
        private const char UrlPathSeparator = '/';

        public RssSourceRepository(IAppDbContext context, ILogger<RssSourceRepository> logger) // ✅ Logger injected
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger)); // ✅ Logger initialized

            // ✅ Define the database retry policy
            _dbRetryPolicy = Policy
                .Handle<DbException>(ex => !(ex is Microsoft.Data.SqlClient.SqlException sqlEx && (sqlEx.Number == 1205 || sqlEx.Number == 1219))) // Handle transient DB errors
                .Or<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException)) // General transient errors, excluding cancellation
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception, "PollyDbRetry: RssSourceRepository database operation failed. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }

        #region Read Operations

        /// <inheritdoc />
        public async Task<RssSource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("RssSourceRepository: Fetching RssSource by ID: {Id}", id);
            return await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                return await _context.RssSources
                    .FirstOrDefaultAsync(rs => rs.Id == id, cancellationToken);
            });
        }

        /// <inheritdoc />
        public async Task<RssSource?> GetByUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("RssSourceRepository: GetByUrlAsync called with null or empty URL.");
                return null;
            }

            string normalizedUrl = NormalizeUrlForComparison(url);
            _logger.LogTrace("RssSourceRepository: Fetching RssSource by Normalized URL: {NormalizedUrl} (Original: {OriginalUrl})", normalizedUrl, url);

            return await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                return await _context.RssSources
                    .FirstOrDefaultAsync(rs => rs.Url == normalizedUrl || rs.Url == url.Trim(), cancellationToken);
            });
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RssSource>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("RssSourceRepository: Fetching all RssSources, ordered by SourceName, AsNoTracking.");
            return await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                return await _context.RssSources
                    .AsNoTracking()
                    .OrderBy(rs => rs.SourceName)
                    .ToListAsync(cancellationToken);
            });
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RssSource>> GetActiveSourcesAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("RssSourceRepository: Fetching all active RssSources, ordered by SourceName, AsNoTracking.");
            return await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                return await _context.RssSources
                    .Where(rs => rs.IsActive)
                    .AsNoTracking()
                    .OrderBy(rs => rs.SourceName)
                    .ToListAsync(cancellationToken);
            });
        }

        #endregion

        #region Write Operations

        /// <inheritdoc />
        public async Task AddAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null)
            {
                _logger.LogError("RssSourceRepository: Attempted to add a null RssSource object.");
                throw new ArgumentNullException(nameof(rssSource));
            }

            rssSource.Url = SanitizeAndNormalizeUrl(rssSource.Url);
            rssSource.SourceName = SanitizeString(rssSource.SourceName);

            DateTime now = DateTime.UtcNow;
            rssSource.CreatedAt = now;
            rssSource.UpdatedAt = now;

            _logger.LogInformation("RssSourceRepository: Adding new RssSource. Name: {SourceName}, URL: {Url}", rssSource.SourceName, rssSource.Url);
            await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                _ = await _context.RssSources.AddAsync(rssSource, cancellationToken);
            });
        }

        /// <inheritdoc />
        public Task UpdateAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null)
            {
                _logger.LogError("RssSourceRepository: Attempted to update with a null RssSource object.");
                throw new ArgumentNullException(nameof(rssSource));
            }

            rssSource.Url = SanitizeAndNormalizeUrl(rssSource.Url);
            rssSource.SourceName = SanitizeString(rssSource.SourceName);

            rssSource.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("RssSourceRepository: Marking RssSource for update. ID: {Id}, Name: {SourceName}, URL: {Url}", rssSource.Id, rssSource.SourceName, rssSource.Url);
            Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<RssSource> entry = _context.RssSources.Entry(rssSource);
            if (entry.State == EntityState.Detached)
            {
                _ = _context.RssSources.Attach(rssSource);
            }
            entry.State = EntityState.Modified;

            return Task.CompletedTask; // SaveChangesAsync will be called by UoW/Service.
        }

        /// <inheritdoc />
        public Task DeleteAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null)
            {
                _logger.LogError("RssSourceRepository: Attempted to delete a null RssSource object.");
                throw new ArgumentNullException(nameof(rssSource));
            }

            _logger.LogInformation("RssSourceRepository: Marking RssSource for deletion. ID: {Id}, Name: {SourceName}", rssSource.Id, rssSource.SourceName);
            _ = _context.RssSources.Remove(rssSource);
            return Task.CompletedTask; // SaveChangesAsync will be called by UoW/Service.
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("RssSourceRepository: Attempting to delete RssSource by ID: {Id}", id);
            return await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                RssSource? sourceToDelete = await GetByIdAsync(id, cancellationToken); // Uses GetByIdAsync which is also Polly-protected.
                if (sourceToDelete == null)
                {
                    _logger.LogWarning("RssSourceRepository: RssSource with ID {Id} not found for deletion.", id);
                    return false;
                }
                _ = _context.RssSources.Remove(sourceToDelete);
                return true;
            });
        }

        #endregion

        #region Existence Checks

        /// <inheritdoc />
        public async Task<bool> ExistsByUrlAsync(string url, Guid? excludeId = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("RssSourceRepository: ExistsByUrlAsync called with null or empty URL.");
                return false;
            }

            string normalizedUrl = NormalizeUrlForComparison(url);
            _logger.LogTrace("RssSourceRepository: Checking existence by Normalized URL: {NormalizedUrl} (Original: {OriginalUrl}), ExcludeID: {ExcludeId}", normalizedUrl, url, excludeId);

            return await _dbRetryPolicy.ExecuteAsync(async () => // ✅ Polly applied
            {
                IQueryable<RssSource> query = _context.RssSources
                    .Where(rs => rs.Url == normalizedUrl || rs.Url == url.Trim());

                if (excludeId.HasValue)
                {
                    query = query.Where(rs => rs.Id != excludeId.Value);
                }
                return await query.AnyAsync(cancellationToken);
            });
        }

        #endregion

        #region Helper Methods for Sanitization/Normalization (Private)
        private string NormalizeUrlForComparison(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            UriBuilder uriBuilder = new(url.Trim());
            uriBuilder.Scheme = uriBuilder.Scheme.ToLowerInvariant();
            uriBuilder.Host = uriBuilder.Host.ToLowerInvariant();

            if (uriBuilder.Host.StartsWith("www.")) { uriBuilder.Host = uriBuilder.Host[4..]; }

            string path = uriBuilder.Path.TrimEnd(UrlPathSeparator);
            uriBuilder.Path = string.IsNullOrEmpty(path) || path == "/" ? "/" : path;

            if ((uriBuilder.Scheme == "http" && uriBuilder.Port == 80) || (uriBuilder.Scheme == "https" && uriBuilder.Port == 443)) { uriBuilder.Port = -1; }

            return uriBuilder.Uri.AbsoluteUri.TrimEnd(UrlPathSeparator);
        }

        private string SanitizeAndNormalizeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) { _logger.LogWarning("RssSourceRepository: URL provided for sanitization was null or empty. Returning empty string."); return string.Empty; }
            string trimmedUrl = url.Trim();
            try
            {
                UriBuilder uriBuilder = new(trimmedUrl);
                if (string.IsNullOrWhiteSpace(uriBuilder.Scheme)) { uriBuilder.Scheme = "https"; uriBuilder.Port = -1; _logger.LogInformation("RssSourceRepository: URL '{OriginalUrl}' had no scheme, defaulted to https. New URL: '{NewUrl}'", trimmedUrl, uriBuilder.Uri.AbsoluteUri); }
                return NormalizeUrlForComparison(uriBuilder.Uri.AbsoluteUri);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "RssSourceRepository: Invalid URL format for '{Url}'. Cannot sanitize/normalize. Returning original trimmed URL.", trimmedUrl);
                return trimmedUrl;
            }
        }

        private string SanitizeString(string? inputString)
        {
            if (string.IsNullOrWhiteSpace(inputString)) { _logger.LogTrace("RssSourceRepository: Input string for sanitization was null or empty. Returning empty string."); return string.Empty; }
            return inputString.Trim();
        }
        #endregion
    }
}