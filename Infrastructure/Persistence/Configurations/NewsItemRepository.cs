// File: Infrastructure/Persistence/Repositories/NewsItemRepository.cs
#region Usings
// Standard .NET & NuGet
using Dapper; // Dapper for micro-ORM operations
using Microsoft.Data.SqlClient; // SQL Server specific connection
using System.Data; // Common Ado.Net interfaces like IDbConnection
using Microsoft.Extensions.Configuration; // To access connection strings
using Microsoft.Extensions.Logging; // For logging
using Polly; // For resilience policies
using Polly.Retry; // For retry policies
using System.Data.Common; // For DbException (base class for database exceptions)
using System.Linq.Expressions; // <--- ADDED THIS BACK: Needed for Expression<> type in FindAsync signature
// Project specific
using Application.Common.Interfaces; // For INewsItemRepository
using Domain.Entities;               // For NewsItem, RssSource, SignalCategory
using Shared.Extensions; // For Truncate extension method
using Shared.Exceptions; // For custom RepositoryException
#endregion

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// Implements the INewsItemRepository for data operations related to NewsItem entities
    /// using Dapper.
    /// </summary>
    public class NewsItemRepository : INewsItemRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<NewsItemRepository> _logger;
        private readonly AsyncRetryPolicy _retryPolicy; // Polly policy for DB operations

        // --- Internal DTOs for Dapper Mapping ---
        private class NewsItemDbDto
        {
            public Guid Id { get; set; }
            public string Title { get; set; } = default!;
            public string Link { get; set; } = default!;
            public string? Summary { get; set; }
            public string? FullContent { get; set; }
            public string? ImageUrl { get; set; }
            public DateTime PublishedDate { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? LastProcessedAt { get; set; }
            public string? SourceName { get; set; }
            public string? SourceItemId { get; set; }
            public double? SentimentScore { get; set; }
            public string? SentimentLabel { get; set; }
            public string? DetectedLanguage { get; set; }
            public string? AffectedAssets { get; set; }
            public Guid RssSourceId { get; set; }
            public bool IsVipOnly { get; set; }
            public Guid? AssociatedSignalCategoryId { get; set; }

            // Properties for RssSource (flat mapping if selected in main query)
            // Aliases like 'RssSource_Id' and 'RssSource_SourceName' are used in SQL to prevent name clashes.
            public Guid RssSource_Id { get; set; } // Matches Id of RssSource
            public string? RssSource_Url { get; set; }
            public string? RssSource_SourceName { get; set; } // Aliased name for RssSource.SourceName
            public bool RssSource_IsActive { get; set; } // Example: if you need more RssSource fields
            public DateTime RssSource_CreatedAt { get; set; }
            public DateTime? RssSource_UpdatedAt { get; set; }
            public string? RssSource_LastModifiedHeader { get; set; }
            public string? RssSource_ETag { get; set; }
            public DateTime? RssSource_LastFetchAttemptAt { get; set; }
            public DateTime? RssSource_LastSuccessfulFetchAt { get; set; }
            public int? RssSource_FetchIntervalMinutes { get; set; }
            public int RssSource_FetchErrorCount { get; set; }
            public string? RssSource_Description { get; set; }
            public Guid? RssSource_DefaultSignalCategoryId { get; set; }


            // Properties for AssociatedSignalCategory (flat mapping if selected in main query)
            public Guid? AssociatedSignalCategory_Id { get; set; } // Matches Id of SignalCategory
            public string? AssociatedSignalCategory_Name { get; set; } // Aliased name for SignalCategory.Name
            public string? AssociatedSignalCategory_Description { get; set; }
            public bool AssociatedSignalCategory_IsActive { get; set; }
            public int AssociatedSignalCategory_SortOrder { get; set; }


            public NewsItem ToDomainEntity()
            {
                var newsItem = new NewsItem
                {
                    Id = Id,
                    Title = Title,
                    Link = Link,
                    Summary = Summary,
                    FullContent = FullContent,
                    ImageUrl = ImageUrl,
                    PublishedDate = PublishedDate,
                    CreatedAt = CreatedAt,
                    LastProcessedAt = LastProcessedAt,
                    SourceName = SourceName, // This directly maps to NewsItem.SourceName
                    SourceItemId = SourceItemId,
                    SentimentScore = SentimentScore,
                    SentimentLabel = SentimentLabel,
                    DetectedLanguage = DetectedLanguage,
                    AffectedAssets = AffectedAssets,
                    RssSourceId = RssSourceId,
                    IsVipOnly = IsVipOnly,
                    AssociatedSignalCategoryId = AssociatedSignalCategoryId
                };

                // Manually reconstruct RssSource and SignalCategory if their properties were selected
                // Check if RssSource_Id has a value (it should if JOIN returned data)
                if (RssSource_Id != Guid.Empty)
                {
                    newsItem.RssSource = new RssSource
                    {
                        Id = RssSource_Id,
                        Url = RssSource_Url ?? string.Empty,
                        SourceName = RssSource_SourceName ?? string.Empty,
                        IsActive = RssSource_IsActive,
                        CreatedAt = RssSource_CreatedAt,
                        UpdatedAt = RssSource_UpdatedAt,
                        LastModifiedHeader = RssSource_LastModifiedHeader,
                        ETag = RssSource_ETag,
                        LastFetchAttemptAt = RssSource_LastFetchAttemptAt,
                        LastSuccessfulFetchAt = RssSource_LastSuccessfulFetchAt,
                        FetchIntervalMinutes = RssSource_FetchIntervalMinutes,
                        FetchErrorCount = RssSource_FetchErrorCount,
                        Description = RssSource_Description,
                        DefaultSignalCategoryId = RssSource_DefaultSignalCategoryId
                    };
                }
                // Check if AssociatedSignalCategory_Id has a value
                if (AssociatedSignalCategory_Id.HasValue && AssociatedSignalCategory_Id.Value != Guid.Empty)
                {
                    newsItem.AssociatedSignalCategory = new SignalCategory
                    {
                        Id = AssociatedSignalCategory_Id.Value,
                        Name = AssociatedSignalCategory_Name ?? string.Empty,
                        Description = AssociatedSignalCategory_Description,
                        IsActive = AssociatedSignalCategory_IsActive,
                        SortOrder = AssociatedSignalCategory_SortOrder
                    };
                }

                return newsItem;
            }
        }

        // DTO for RssSource when fetched separately or in multi-mapping
        // This is not used for primary NewsItem queries, but for explicit RssSource fetches
        private class RssSourceMapDto // Consider renaming to RssSourceDto for clarity
        {
            public Guid Id { get; set; }
            public string Url { get; set; } = default!;
            public string SourceName { get; set; } = default!;
            public bool IsActive { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
            public string? LastModifiedHeader { get; set; }
            public string? ETag { get; set; }
            public DateTime? LastFetchAttemptAt { get; set; }
            public DateTime? LastSuccessfulFetchAt { get; set; }
            public int? FetchIntervalMinutes { get; set; }
            public int FetchErrorCount { get; set; }
            public string? Description { get; set; }
            public Guid? DefaultSignalCategoryId { get; set; }

            public RssSource ToDomainEntity()
            {
                return new RssSource
                {
                    Id = Id,
                    Url = Url,
                    SourceName = SourceName,
                    IsActive = IsActive,
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                    LastModifiedHeader = LastModifiedHeader,
                    ETag = ETag,
                    LastFetchAttemptAt = LastFetchAttemptAt,
                    LastSuccessfulFetchAt = LastSuccessfulFetchAt,
                    FetchIntervalMinutes = FetchIntervalMinutes,
                    FetchErrorCount = FetchErrorCount,
                    Description = Description,
                    DefaultSignalCategoryId = DefaultSignalCategoryId
                };
            }
        }

        // DTO for SignalCategory when fetched separately or in multi-mapping
        private class SignalCategoryMapDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = default!;
            public string? Description { get; set; }
            public bool IsActive { get; set; }
            public int SortOrder { get; set; }

            public SignalCategory ToDomainEntity()
            {
                return new SignalCategory
                {
                    Id = Id,
                    Name = Name,
                    Description = Description,
                    IsActive = IsActive,
                    SortOrder = SortOrder
                };
            }
        }

        #region Constructor
        public NewsItemRepository(IConfiguration configuration, ILogger<NewsItemRepository> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new ArgumentNullException("DefaultConnection", "DefaultConnection string not found.");

            // Polly configuration for transient errors (e.g., network issues, temporary DB unavailability)
            _retryPolicy = Policy
                .Handle<DbException>(ex => !(ex is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))) // SQL Server PK/Unique constraint violation
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "NewsItemRepository: Transient database error encountered. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }
        #endregion

        // Helper to create a new SqlConnection
        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        #region SearchNewsAsync Implementation
        /// <summary>
        /// Searches for news items based on keywords, date range, and pagination.
        /// </summary>
        public async Task<(List<NewsItem> Items, int TotalCount)> SearchNewsAsync(
            IEnumerable<string> keywords,
            DateTime sinceDate,
            DateTime untilDate,
            int pageNumber,
            int pageSize,
            bool matchAllKeywords = false, // Default is OR search for keywords
            bool isUserVip = false,        // To filter by NewsItem.IsVipOnly
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("SearchNewsAsync called. Keywords: [{Keywords}], Since: {SinceDate}, Until: {UntilDate}, Page: {Page}, PageSize: {PageSize}, IsVIP: {IsVip}",
                string.Join(",", keywords ?? Enumerable.Empty<string>()), sinceDate, untilDate, pageNumber, pageSize, isUserVip);

            if (pageNumber <= 0) pageNumber = 1;
            if (pageSize <= 0) pageSize = 10; // Default page size

            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var whereClauses = new List<string>();
                    var parameters = new DynamicParameters();

                    // Date filtering
                    whereClauses.Add("n.PublishedDate >= @SinceDate");
                    whereClauses.Add("n.PublishedDate <= @UntilDate");
                    parameters.Add("SinceDate", sinceDate);
                    parameters.Add("UntilDate", untilDate);

                    // VIP filtering
                    if (!isUserVip)
                    {
                        whereClauses.Add("n.IsVipOnly = 0"); // SQL Server boolean false
                    }

                    // Keyword filtering
                    var keywordList = keywords?.Select(k => k.Trim()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                    if (keywordList != null && keywordList.Any())
                    {
                        var keywordConditions = new List<string>();
                        for (int i = 0; i < keywordList.Count; i++)
                        {
                            var keyword = keywordList[i];
                            var paramName = $"keyword{i}";
                            // Use COLLATE for case-insensitive search if your database is not configured as such by default
                            // Alternatively, ensure your DB collation is case-insensitive (e.g., SQL_Latin1_General_CP1_CI_AS)
                            keywordConditions.Add($"(LOWER(n.Title) LIKE '%' + LOWER(@{paramName}) + '%') OR (LOWER(n.Summary) LIKE '%' + LOWER(@{paramName}) + '%') OR (LOWER(n.FullContent) LIKE '%' + LOWER(@{paramName}) + '%')");
                            parameters.Add(paramName, keyword);
                        }

                        if (matchAllKeywords) // AND logic
                        {
                            whereClauses.Add($"({string.Join(" AND ", keywordConditions)})");
                        }
                        else // OR logic (default)
                        {
                            whereClauses.Add($"({string.Join(" OR ", keywordConditions)})");
                        }
                    }

                    var fullWhereClause = whereClauses.Any() ? " WHERE " + string.Join(" AND ", whereClauses) : "";

                    // Total Count Query
                    var countSql = $"SELECT COUNT(n.Id) FROM NewsItems n {fullWhereClause};";
                    var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);

                    if (totalCount == 0)
                    {
                        return (new List<NewsItem>(), 0);
                    }

                    // Main Search Query with Pagination and Ordering
                    var sql = $@"
                        SELECT
                            n.Id, n.Title, n.Link, n.Summary, n.FullContent, n.ImageUrl, n.PublishedDate, n.CreatedAt, n.LastProcessedAt,
                            n.SourceName, n.SourceItemId, n.SentimentScore, n.SentimentLabel, n.DetectedLanguage, n.AffectedAssets,
                            n.RssSourceId, n.IsVipOnly, n.AssociatedSignalCategoryId,
                            rs.Id AS RssSource_Id, rs.Url AS RssSource_Url, rs.SourceName AS RssSource_SourceName, rs.IsActive AS RssSource_IsActive, rs.CreatedAt AS RssSource_CreatedAt, rs.UpdatedAt AS RssSource_UpdatedAt, rs.LastModifiedHeader AS RssSource_LastModifiedHeader, rs.ETag AS RssSource_ETag, rs.LastFetchAttemptAt AS RssSource_LastFetchAttemptAt, rs.LastSuccessfulFetchAt AS RssSource_LastSuccessfulFetchAt, rs.FetchIntervalMinutes AS RssSource_FetchIntervalMinutes, rs.FetchErrorCount AS RssSource_FetchErrorCount, rs.Description AS RssSource_Description, rs.DefaultSignalCategoryId AS RssSource_DefaultSignalCategoryId,
                            sc.Id AS AssociatedSignalCategory_Id, sc.Name AS AssociatedSignalCategory_Name, sc.Description AS AssociatedSignalCategory_Description, sc.IsActive AS AssociatedSignalCategory_IsActive, sc.SortOrder AS AssociatedSignalCategory_SortOrder
                        FROM NewsItems n
                        LEFT JOIN RssSources rs ON n.RssSourceId = rs.Id
                        LEFT JOIN SignalCategories sc ON n.AssociatedSignalCategoryId = sc.Id
                        {fullWhereClause}
                        ORDER BY n.PublishedDate DESC, n.CreatedAt DESC
                        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

                    parameters.Add("Offset", (pageNumber - 1) * pageSize);
                    parameters.Add("PageSize", pageSize);

                    // Multi-mapping to reconstruct NewsItem with RssSource and AssociatedSignalCategory
                    // Use a Dictionary to keep track of unique NewsItem entities and avoid duplicates from joins.
                    var newsItemsMap = new Dictionary<Guid, NewsItem>();

                    var items = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        sql,
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            if (!newsItemsMap.TryGetValue(newsItemDto.Id, out var newsItem))
                            {
                                newsItem = newsItemDto.ToDomainEntity(); // NewsItemDbDto includes RssSource and SignalCategory aliased fields
                                newsItemsMap.Add(newsItem.Id, newsItem);
                            }

                            // The ToDomainEntity in NewsItemDbDto should handle populating NewsItem.RssSource and NewsItem.AssociatedSignalCategory
                            // based on the aliased properties it receives.
                            // So, no need to manually set newsItem.RssSource or newsItem.AssociatedSignalCategory here from rssSourceDto or signalCategoryDto.
                            // The direct properties on NewsItemDbDto (RssSource_Id, RssSource_Url etc.) should have been used by NewsItemDbDto.ToDomainEntity()

                            return newsItem; // Dapper still needs a return value from the delegate
                        },
                        param: parameters,
                        splitOn: "RssSource_Id,AssociatedSignalCategory_Id" // These mark where the next DTO starts in the flattened row
                    );

                    _logger.LogDebug("SearchNewsAsync found {TotalCount} items, returning page {PageNumber} with {PageItemCount} items.", totalCount, pageNumber, newsItemsMap.Values.Count);
                    return (newsItemsMap.Values.ToList(), totalCount);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchNewsAsync operation.");
                throw new RepositoryException("Failed to search news items.", ex);
            }
        }
        #endregion

        #region INewsItemRepository Read Operations
        public async Task<NewsItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching NewsItem by ID: {NewsItemId}", id);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = @"
                        SELECT
                            n.Id, n.Title, n.Link, n.Summary, n.FullContent, n.ImageUrl, n.PublishedDate, n.CreatedAt, n.LastProcessedAt,
                            n.SourceName, n.SourceItemId, n.SentimentScore, n.SentimentLabel, n.DetectedLanguage, n.AffectedAssets,
                            n.RssSourceId, n.IsVipOnly, n.AssociatedSignalCategoryId,
                            rs.Id AS RssSource_Id, rs.Url AS RssSource_Url, rs.SourceName AS RssSource_SourceName, rs.IsActive AS RssSource_IsActive, rs.CreatedAt AS RssSource_CreatedAt, rs.UpdatedAt AS RssSource_UpdatedAt, rs.LastModifiedHeader AS RssSource_LastModifiedHeader, rs.ETag AS RssSource_ETag, rs.LastFetchAttemptAt AS RssSource_LastFetchAttemptAt, rs.LastSuccessfulFetchAt AS RssSource_LastSuccessfulFetchAt, rs.FetchIntervalMinutes AS RssSource_FetchIntervalMinutes, rs.FetchErrorCount AS RssSource_FetchErrorCount, rs.Description AS RssSource_Description, rs.DefaultSignalCategoryId AS RssSource_DefaultSignalCategoryId,
                            sc.Id AS AssociatedSignalCategory_Id, sc.Name AS AssociatedSignalCategory_Name, sc.Description AS AssociatedSignalCategory_Description, sc.IsActive AS AssociatedSignalCategory_IsActive, sc.SortOrder AS AssociatedSignalCategory_SortOrder
                        FROM NewsItems n
                        LEFT JOIN RssSources rs ON n.RssSourceId = rs.Id
                        LEFT JOIN SignalCategories sc ON n.AssociatedSignalCategoryId = sc.Id
                        WHERE n.Id = @Id;";

                    var newsItem = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        sql,
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            var item = newsItemDto.ToDomainEntity();
                            // NewsItemDbDto's ToDomainEntity should already handle the RssSource and SignalCategory mapping from aliased properties
                            // The rssSourceDto and signalCategoryDto are passed as separate objects by Dapper but aren't strictly needed for the NewsItem's own hydration
                            // if NewsItemDbDto's ToDomainEntity is designed to map them from its own aliased properties.
                            return item;
                        },
                        param: new { Id = id },
                        splitOn: "RssSource_Id,AssociatedSignalCategory_Id"
                    );

                    return newsItem.FirstOrDefault();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching NewsItem by ID {NewsItemId}.", id);
                throw new RepositoryException($"Failed to get news item by ID '{id}'.", ex);
            }
        }

        public async Task<NewsItem?> GetBySourceDetailsAsync(Guid rssSourceId, string sourceItemId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceItemId)) return null;
            _logger.LogDebug("Fetching NewsItem by RssSourceId: {RssSourceId} and SourceItemId: {SourceItemId}", rssSourceId, sourceItemId);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = @"
                        SELECT
                            n.Id, n.Title, n.Link, n.Summary, n.FullContent, n.ImageUrl, n.PublishedDate, n.CreatedAt, n.LastProcessedAt,
                            n.SourceName, n.SourceItemId, n.SentimentScore, n.SentimentLabel, n.DetectedLanguage, n.AffectedAssets,
                            n.RssSourceId, n.IsVipOnly, n.AssociatedSignalCategoryId,
                            rs.Id AS RssSource_Id, rs.Url AS RssSource_Url, rs.SourceName AS RssSource_SourceName, rs.IsActive AS RssSource_IsActive, rs.CreatedAt AS RssSource_CreatedAt, rs.UpdatedAt AS RssSource_UpdatedAt, rs.LastModifiedHeader AS RssSource_LastModifiedHeader, rs.ETag AS RssSource_ETag, rs.LastFetchAttemptAt AS RssSource_LastFetchAttemptAt, rs.LastSuccessfulFetchAt AS RssSource_LastSuccessfulFetchAt, rs.FetchIntervalMinutes AS RssSource_FetchIntervalMinutes, rs.FetchErrorCount AS RssSource_FetchErrorCount, rs.Description AS RssSource_Description, rs.DefaultSignalCategoryId AS RssSource_DefaultSignalCategoryId,
                            sc.Id AS AssociatedSignalCategory_Id, sc.Name AS AssociatedSignalCategory_Name, sc.Description AS AssociatedSignalCategory_Description, sc.IsActive AS AssociatedSignalCategory_IsActive, sc.SortOrder AS AssociatedSignalCategory_SortOrder
                        FROM NewsItems n
                        LEFT JOIN RssSources rs ON n.RssSourceId = rs.Id
                        LEFT JOIN SignalCategories sc ON n.AssociatedSignalCategoryId = sc.Id
                        WHERE n.RssSourceId = @RssSourceId AND n.SourceItemId = @SourceItemId;";

                    var newsItem = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        sql,
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            var item = newsItemDto.ToDomainEntity();
                            return item;
                        },
                        param: new { RssSourceId = rssSourceId, SourceItemId = sourceItemId },
                        splitOn: "RssSource_Id,AssociatedSignalCategory_Id"
                    );

                    return newsItem.FirstOrDefault();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching NewsItem by source details RssSourceId: {RssSourceId}, SourceItemId: {SourceItemId}.", rssSourceId, sourceItemId);
                throw new RepositoryException($"Failed to get news item by source details '{rssSourceId}', '{sourceItemId}'.", ex);
            }
        }

        public async Task<bool> ExistsBySourceDetailsAsync(Guid rssSourceId, string sourceItemId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceItemId)) return false;
            _logger.LogDebug("Checking existence of NewsItem by RssSourceId: {RssSourceId} and SourceItemId: {SourceItemId}", rssSourceId, sourceItemId);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = "SELECT COUNT(*) FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId = @SourceItemId;";
                    var count = await connection.ExecuteScalarAsync<int>(sql, new { RssSourceId = rssSourceId, SourceItemId = sourceItemId });
                    return count > 0;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking existence of NewsItem by source details RssSourceId: {RssSourceId}, SourceItemId: {SourceItemId}.", rssSourceId, sourceItemId);
                throw new RepositoryException($"Failed to check existence of news item by source details '{rssSourceId}', '{sourceItemId}'.", ex);
            }
        }

        public async Task<IEnumerable<NewsItem>> GetRecentNewsAsync(
            int count,
            Guid? rssSourceId = null,
            bool includeRssSource = false, // This hint is less relevant for Dapper, joins are explicit.
            CancellationToken cancellationToken = default)
        {
            if (count <= 0) return Enumerable.Empty<NewsItem>();

            _logger.LogDebug("Fetching {Count} recent news items. RssSourceIdFilter: {RssSourceIdFilter}",
                count, rssSourceId?.ToString() ?? "Any");

            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = @"
                        SELECT
                            n.Id, n.Title, n.Link, n.Summary, n.FullContent, n.ImageUrl, n.PublishedDate, n.CreatedAt, n.LastProcessedAt,
                            n.SourceName, n.SourceItemId, n.SentimentScore, n.SentimentLabel, n.DetectedLanguage, n.AffectedAssets,
                            n.RssSourceId, n.IsVipOnly, n.AssociatedSignalCategoryId,
                            rs.Id AS RssSource_Id, rs.Url AS RssSource_Url, rs.SourceName AS RssSource_SourceName, rs.IsActive AS RssSource_IsActive, rs.CreatedAt AS RssSource_CreatedAt, rs.UpdatedAt AS RssSource_UpdatedAt, rs.LastModifiedHeader AS RssSource_LastModifiedHeader, rs.ETag AS RssSource_ETag, rs.LastFetchAttemptAt AS RssSource_LastFetchAttemptAt, rs.LastSuccessfulFetchAt AS RssSource_LastSuccessfulFetchAt, rs.FetchIntervalMinutes AS RssSource_FetchIntervalMinutes, rs.FetchErrorCount AS RssSource_FetchErrorCount, rs.Description AS RssSource_Description, rs.DefaultSignalCategoryId AS RssSource_DefaultSignalCategoryId,
                            sc.Id AS AssociatedSignalCategory_Id, sc.Name AS AssociatedSignalCategory_Name, sc.Description AS AssociatedSignalCategory_Description, sc.IsActive AS AssociatedSignalCategory_IsActive, sc.SortOrder AS AssociatedSignalCategory_SortOrder
                        FROM NewsItems n
                        LEFT JOIN RssSources rs ON n.RssSourceId = rs.Id
                        LEFT JOIN SignalCategories sc ON n.AssociatedSignalCategoryId = sc.Id";

                    var whereClauses = new List<string>();
                    var parameters = new DynamicParameters();

                    if (rssSourceId.HasValue)
                    {
                        whereClauses.Add("n.RssSourceId = @RssSourceId");
                        parameters.Add("RssSourceId", rssSourceId.Value);
                    }

                    if (whereClauses.Any())
                    {
                        sql += " WHERE " + string.Join(" AND ", whereClauses);
                    }

                    sql += @"
                        ORDER BY n.PublishedDate DESC, n.CreatedAt DESC
                        OFFSET 0 ROWS FETCH NEXT @Count ROWS ONLY;"; // Always fetch from start for 'recent'
                    parameters.Add("Count", count);

                    var newsItems = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        sql,
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            var item = newsItemDto.ToDomainEntity();
                            return item;
                        },
                        param: parameters,
                        splitOn: "RssSource_Id,AssociatedSignalCategory_Id"
                    );

                    return newsItems.ToList();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching recent news items (Count: {Count}, RssSourceId: {RssSourceId}).", count, rssSourceId);
                throw new RepositoryException($"Failed to get recent news items (Count: {count}, RssSourceId: {rssSourceId}).", ex);
            }
        }

        // Method to satisfy the interface, but explicitly not supported by Dapper's core.
        public Task<IEnumerable<NewsItem>> FindAsync(
            Expression<Func<NewsItem, bool>> predicate,
            bool includeRssSource = false,
            CancellationToken cancellationToken = default)
        {
            _logger.LogError("NewsItemRepository: FindAsync with Expression<Func<NewsItem, bool>> is NOT SUPPORTED by Dapper directly. " +
                             "This method will throw a NotSupportedException.");
            throw new NotSupportedException("Arbitrary LINQ Expression predicates are not supported by this Dapper repository. " +
                                            "Please use specific query methods or pass raw SQL conditions from the calling layer.");
        }

        public async Task<HashSet<string>> GetExistingSourceItemIdsAsync(Guid rssSourceId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching existing SourceItemIds for RssSourceId: {RssSourceId}", rssSourceId);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = "SELECT SourceItemId FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId IS NOT NULL;";
                    var ids = (await connection.QueryAsync<string>(sql, new { RssSourceId = rssSourceId })).ToList();
                    return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching existing SourceItemIds for RssSourceId: {RssSourceId}.", rssSourceId);
                throw new RepositoryException($"Failed to get existing source item IDs for RSS source '{rssSourceId}'.", ex);
            }
        }
        #endregion

        #region INewsItemRepository Write Operations
        public async Task AddAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null) throw new ArgumentNullException(nameof(newsItem));
            _logger.LogInformation("Adding new NewsItem. NewsItemId: {NewsItemId}, Title: {Title}", newsItem.Id, newsItem.Title.Truncate(50));
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = @"
                        INSERT INTO NewsItems (
                            Id, Title, Link, Summary, FullContent, ImageUrl, PublishedDate, CreatedAt, LastProcessedAt,
                            SourceName, SourceItemId, SentimentScore, SentimentLabel, DetectedLanguage, AffectedAssets,
                            RssSourceId, IsVipOnly, AssociatedSignalCategoryId
                        ) VALUES (
                            @Id, @Title, @Link, @Summary, @FullContent, @ImageUrl, @PublishedDate, @CreatedAt, @LastProcessedAt,
                            @SourceName, @SourceItemId, @SentimentScore, @SentimentLabel, @DetectedLanguage, @AffectedAssets,
                            @RssSourceId, @IsVipOnly, @AssociatedSignalCategoryId
                        );";

                    await connection.ExecuteAsync(sql, new
                    {
                        newsItem.Id,
                        newsItem.Title,
                        newsItem.Link,
                        newsItem.Summary,
                        newsItem.FullContent,
                        newsItem.ImageUrl,
                        newsItem.PublishedDate,
                        newsItem.CreatedAt,
                        newsItem.LastProcessedAt,
                        newsItem.SourceName,
                        newsItem.SourceItemId,
                        newsItem.SentimentScore,
                        newsItem.SentimentLabel,
                        newsItem.DetectedLanguage,
                        newsItem.AffectedAssets,
                        newsItem.RssSourceId,
                        newsItem.IsVipOnly,
                        newsItem.AssociatedSignalCategoryId
                    });
                });
                _logger.LogInformation("Successfully added NewsItem: {NewsItemId}", newsItem.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding NewsItem {NewsItemId} to the database.", newsItem.Id);
                throw new RepositoryException($"Failed to add news item '{newsItem.Id}'.", ex);
            }
        }

        public async Task AddRangeAsync(IEnumerable<NewsItem> newsItems, CancellationToken cancellationToken = default)
        {
            if (newsItems == null || !newsItems.Any())
            {
                _logger.LogDebug("AddRangeAsync called with no news items to add.");
                return;
            }
            _logger.LogInformation("Adding a range of {Count} news items.", newsItems.Count());
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = @"
                        INSERT INTO NewsItems (
                            Id, Title, Link, Summary, FullContent, ImageUrl, PublishedDate, CreatedAt, LastProcessedAt,
                            SourceName, SourceItemId, SentimentScore, SentimentLabel, DetectedLanguage, AffectedAssets,
                            RssSourceId, IsVipOnly, AssociatedSignalCategoryId
                        ) VALUES (
                            @Id, @Title, @Link, @Summary, @FullContent, @ImageUrl, @PublishedDate, @CreatedAt, @LastProcessedAt,
                            @SourceName, @SourceItemId, @SentimentScore, @SentimentLabel, @DetectedLanguage, @AffectedAssets,
                            @RssSourceId, @IsVipOnly, @AssociatedSignalCategoryId
                        );";

                    // Dapper can execute a single SQL statement multiple times for an IEnumerable of parameters
                    // This is efficient for bulk inserts.
                    await connection.ExecuteAsync(sql, newsItems.Select(ni => new
                    {
                        ni.Id,
                        ni.Title,
                        ni.Link,
                        ni.Summary,
                        ni.FullContent,
                        ni.ImageUrl,
                        ni.PublishedDate,
                        ni.CreatedAt,
                        ni.LastProcessedAt,
                        ni.SourceName,
                        ni.SourceItemId,
                        ni.SentimentScore,
                        ni.SentimentLabel,
                        ni.DetectedLanguage,
                        ni.AffectedAssets,
                        ni.RssSourceId,
                        ni.IsVipOnly,
                        ni.AssociatedSignalCategoryId
                    }));
                });
                _logger.LogInformation("Successfully added {Count} news items.", newsItems.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding range of news items to the database.");
                throw new RepositoryException("Failed to add range of news items.", ex);
            }
        }

        public async Task UpdateAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null) throw new ArgumentNullException(nameof(newsItem));
            _logger.LogInformation("Updating NewsItem. NewsItemId: {NewsItemId}", newsItem.Id);
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = @"
                        UPDATE NewsItems SET
                            Title = @Title,
                            Link = @Link,
                            Summary = @Summary,
                            FullContent = @FullContent,
                            ImageUrl = @ImageUrl,
                            PublishedDate = @PublishedDate,
                            LastProcessedAt = @LastProcessedAt,
                            SourceName = @SourceName,
                            SourceItemId = @SourceItemId,
                            SentimentScore = @SentimentScore,
                            SentimentLabel = @SentimentLabel,
                            DetectedLanguage = @DetectedLanguage,
                            AffectedAssets = @AffectedAssets,
                            RssSourceId = @RssSourceId,
                            IsVipOnly = @IsVipOnly,
                            AssociatedSignalCategoryId = @AssociatedSignalCategoryId
                        WHERE Id = @Id;";

                    var rowsAffected = await connection.ExecuteAsync(sql, new
                    {
                        newsItem.Title,
                        newsItem.Link,
                        newsItem.Summary,
                        newsItem.FullContent,
                        newsItem.ImageUrl,
                        newsItem.PublishedDate,
                        newsItem.LastProcessedAt,
                        newsItem.SourceName,
                        newsItem.SourceItemId,
                        newsItem.SentimentScore,
                        newsItem.SentimentLabel,
                        newsItem.DetectedLanguage,
                        newsItem.AffectedAssets,
                        newsItem.RssSourceId,
                        newsItem.IsVipOnly,
                        newsItem.AssociatedSignalCategoryId,
                        newsItem.Id // WHERE clause parameter
                    });

                    if (rowsAffected == 0)
                    {
                        // If no rows affected, it means the item was not found or was concurrently deleted/modified.
                        throw new InvalidOperationException($"NewsItem with ID '{newsItem.Id}' not found for update, or no changes were made. Concurrency conflict suspected.");
                    }
                });
                _logger.LogInformation("Successfully updated NewsItem: {NewsItemId}", newsItem.Id);
            }
            catch (InvalidOperationException ex) // Catch concurrency specific error
            {
                _logger.LogError(ex, "Concurrency conflict or NewsItem not found for update. NewsItemId: {NewsItemId}", newsItem.Id);
                throw; // Re-throw to propagate this specific error type
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating NewsItem {NewsItemId} in the database.", newsItem.Id);
                throw new RepositoryException($"Failed to update news item '{newsItem.Id}'.", ex);
            }
        }

        public async Task DeleteAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null) throw new ArgumentNullException(nameof(newsItem));
            _logger.LogInformation("Removing NewsItem. NewsItemId: {NewsItemId}", newsItem.Id);
            await DeleteByIdAsync(newsItem.Id, cancellationToken); // Delegate to DeleteByIdAsync
        }

        public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to delete NewsItem by ID: {NewsItemId}", id);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    // Note: If NewsItem has other entities that cascade delete, deleting the NewsItem will handle it.
                    // For performance, a simple DELETE is often best if cascades are set in DB.
                    var sql = "DELETE FROM NewsItems WHERE Id = @Id;";
                    var rowsAffected = await connection.ExecuteAsync(sql, new { Id = id });

                    if (rowsAffected == 0)
                    {
                        _logger.LogWarning("NewsItem with ID {NewsItemId} not found for deletion.", id);
                        return false;
                    }

                    _logger.LogInformation("Successfully deleted NewsItem with ID: {NewsItemId}", id);
                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting NewsItem with ID {NewsItemId} from the database.", id);
                throw new RepositoryException($"Failed to delete news item with ID '{id}'.", ex);
            }
        }
        #endregion
    }
}