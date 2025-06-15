// File: Infrastructure/Persistence/Repositories/NewsItemRepository.cs
#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces; // For INewsItemRepository
using Dapper; // Dapper for micro-ORM operations
using Domain.Entities;               // For NewsItem, RssSource, SignalCategory
using Microsoft.Data.SqlClient; // SQL Server specific connection
using Microsoft.Extensions.Configuration; // To access connection strings
using Microsoft.Extensions.Logging; // For logging
using Polly; // For resilience policies
using Polly.Retry; // For retry policies
using Shared.Extensions; // For Truncate extension method
using System.Data; // Common Ado.Net interfaces like IDbConnection
using System.Data.Common; // For DbException (base class for database exceptions)
using System.Linq.Expressions; // <--- CORRECTED: Needed for Expression<> type in FindAsync signature
#endregion

namespace Infrastructure.Persistence.Configurations
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
        private const int CommandTimeoutSeconds = 120; // <--- ADDED: Increased command timeout for potentially long queries

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
            public bool RssSource_IsActive { get; set; }
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
                NewsItem newsItem = new()
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
            bool matchAllKeywords = false,
            bool isUserVip = false,
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("SearchNewsAsync (Fallback LIKE method) called...");

            if (pageNumber <= 0)
            {
                pageNumber = 1;
            }

            if (pageSize <= 0)
            {
                pageSize = 10;
            }

            try
            {
                // Using Polly as before
                return await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(ct);

                    List<string> whereClauses = new();
                    DynamicParameters parameters = new();

                    // --- Date and VIP Filtering ---
                    whereClauses.Add("n.PublishedDate >= @SinceDate AND n.PublishedDate <= @UntilDate");
                    parameters.Add("SinceDate", sinceDate);
                    parameters.Add("UntilDate", untilDate);
                    if (!isUserVip)
                    {
                        whereClauses.Add("n.IsVipOnly = 0");
                    }

                    // ✅✅ --- REVERTING TO THE ORIGINAL, WORKING `LIKE` LOGIC --- ✅✅
                    // This is not the fastest, but it works without any special DB setup.
                    List<string>? keywordList = keywords?.Select(k => k.Trim()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                    if (keywordList != null && keywordList.Any())
                    {
                        List<string> keywordConditions = new();
                        for (int i = 0; i < keywordList.Count; i++)
                        {
                            string keyword = keywordList[i];
                            string paramName = $"keyword{i}";
                            // We search in Title and Summary. Searching in FullContent can be very slow.
                            keywordConditions.Add($"(LOWER(n.Title) LIKE '%' + LOWER(@{paramName}) + '%' OR LOWER(n.Summary) LIKE '%' + LOWER(@{paramName}) + '%')");
                            parameters.Add(paramName, keyword);
                        }

                        string keywordOperator = matchAllKeywords ? " AND " : " OR ";
                        whereClauses.Add($"({string.Join(keywordOperator, keywordConditions)})");
                    }

                    string fullWhereClause = whereClauses.Any() ? "WHERE " + string.Join(" AND ", whereClauses) : "";

                    // --- Back to Two Separate Queries ---
                    // This is necessary because QueryMultiple + Multi-mapping is problematic in Dapper.

                    // Query 1: Get the total count
                    string countSql = $"SELECT COUNT(n.Id) FROM NewsItems n {fullWhereClause};";
                    int totalCount = await connection.ExecuteScalarAsync<int>(
                        new CommandDefinition(countSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)
                    );

                    if (totalCount == 0)
                    {
                        return (new List<NewsItem>(), 0);
                    }

                    // Query 2: Get the paged data
                    string sql = $@"
                SELECT
                    n.Id, n.Title, n.Link, n.Summary, n.FullContent, n.ImageUrl, n.PublishedDate, n.CreatedAt, 
                    n.SourceName, n.IsVipOnly, n.AssociatedSignalCategoryId,
                    rs.Id AS RssSource_Id, rs.SourceName AS RssSource_SourceName,
                    sc.Id AS AssociatedSignalCategory_Id, sc.Name AS AssociatedSignalCategory_Name
                FROM NewsItems n
                LEFT JOIN RssSources rs ON n.RssSourceId = rs.Id
                LEFT JOIN SignalCategories sc ON n.AssociatedSignalCategoryId = sc.Id
                {fullWhereClause}
                ORDER BY n.PublishedDate DESC
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

                    parameters.Add("Offset", (pageNumber - 1) * pageSize);
                    parameters.Add("PageSize", pageSize);

                    // This multi-mapping call is correct and works with a separate query.
                    Dictionary<Guid, NewsItem> newsItemsMap = new();
                    IEnumerable<NewsItem> items = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        new CommandDefinition(sql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct),
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            if (!newsItemsMap.TryGetValue(newsItemDto.Id, out NewsItem? newsItem))
                            {
                                newsItem = newsItemDto.ToDomainEntity();
                                newsItemsMap.Add(newsItem.Id, newsItem);
                            }
                            return newsItem;
                        },
                        splitOn: "RssSource_Id,AssociatedSignalCategory_Id"
                    );

                    return (newsItemsMap.Values.ToList(), totalCount);

                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchNewsAsync operation (using LIKE fallback).");
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
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    string sql = @"
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

                    IEnumerable<NewsItem> newsItem = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        new CommandDefinition(sql, new { Id = id }, commandTimeout: CommandTimeoutSeconds), // <--- ADDED: Pass CommandTimeout
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            NewsItem item = newsItemDto.ToDomainEntity();
                            // NewsItemDbDto's ToDomainEntity should already handle the RssSource and SignalCategory mapping from aliased properties
                            return item;
                        },
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
            if (string.IsNullOrWhiteSpace(sourceItemId))
            {
                return null;
            }

            _logger.LogDebug("Fetching NewsItem by RssSourceId: {RssSourceId} and SourceItemId: {SourceItemId}", rssSourceId, sourceItemId);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    string sql = @"
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

                    IEnumerable<NewsItem> newsItem = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        new CommandDefinition(sql, new { RssSourceId = rssSourceId, SourceItemId = sourceItemId }, commandTimeout: CommandTimeoutSeconds), // <--- ADDED: Pass CommandTimeout
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            NewsItem item = newsItemDto.ToDomainEntity();
                            return item;
                        },
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
            if (string.IsNullOrWhiteSpace(sourceItemId))
            {
                return false;
            }

            _logger.LogDebug("Checking existence of NewsItem by RssSourceId: {RssSourceId} and SourceItemId: {SourceItemId}", rssSourceId, sourceItemId);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    string sql = "SELECT COUNT(*) FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId = @SourceItemId;";
                    int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { RssSourceId = rssSourceId, SourceItemId = sourceItemId }, commandTimeout: CommandTimeoutSeconds)); // <--- ADDED: Pass CommandTimeout
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
            if (count <= 0)
            {
                return Enumerable.Empty<NewsItem>();
            }

            _logger.LogDebug("Fetching {Count} recent news items. RssSourceIdFilter: {RssSourceIdFilter}",
                count, rssSourceId?.ToString() ?? "Any");

            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    string sql = @"
                        SELECT
                            n.Id, n.Title, n.Link, n.Summary, n.FullContent, n.ImageUrl, n.PublishedDate, n.CreatedAt, n.LastProcessedAt,
                            n.SourceName, n.SourceItemId, n.SentimentScore, n.SentimentLabel, n.DetectedLanguage, n.AffectedAssets,
                            n.RssSourceId, n.IsVipOnly, n.AssociatedSignalCategoryId,
                            rs.Id AS RssSource_Id, rs.Url AS RssSource_Url, rs.SourceName AS RssSource_SourceName, rs.IsActive AS RssSource_IsActive, rs.CreatedAt AS RssSource_CreatedAt, rs.UpdatedAt AS RssSource_UpdatedAt, rs.LastModifiedHeader AS RssSource_LastModifiedHeader, rs.ETag AS RssSource_ETag, rs.LastFetchAttemptAt AS RssSource_LastFetchAttemptAt, rs.LastSuccessfulFetchAt AS RssSource_LastSuccessfulFetchAt, rs.FetchIntervalMinutes AS RssSource_FetchIntervalMinutes, rs.FetchErrorCount AS RssSource_FetchErrorCount, rs.Description AS RssSource_Description, rs.DefaultSignalCategoryId AS RssSource_DefaultSignalCategoryId,
                            sc.Id AS AssociatedSignalCategory_Id, sc.Name AS AssociatedSignalCategory_Name, sc.Description AS AssociatedSignalCategory_Description, sc.IsActive AS AssociatedSignalCategory_IsActive, sc.SortOrder AS AssociatedSignalCategory_SortOrder
                        FROM NewsItems n
                        LEFT JOIN RssSources rs ON n.RssSourceId = rs.Id
                        LEFT JOIN SignalCategories sc ON n.AssociatedSignalCategoryId = sc.Id";

                    List<string> whereClauses = new();
                    DynamicParameters parameters = new();

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

                    IEnumerable<NewsItem> newsItems = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        new CommandDefinition(sql, parameters, commandTimeout: CommandTimeoutSeconds), // <--- ADDED: Pass CommandTimeout
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            NewsItem item = newsItemDto.ToDomainEntity();
                            return item;
                        },
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
        // <--- CORRECTED: This method must exist to implement the interface.
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
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    string sql = "SELECT SourceItemId FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId IS NOT NULL;";
                    List<string> ids = (await connection.QueryAsync<string>(new CommandDefinition(sql, new { RssSourceId = rssSourceId }, commandTimeout: CommandTimeoutSeconds))).ToList(); // <--- ADDED: Pass CommandTimeout
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
        /// <summary>
        /// Adds a new NewsItem to the database, but only if it does not already exist.
        /// Duplicates are identified first by a combination of RssSourceId and a non-empty SourceItemId.
        /// If SourceItemId is null or empty, it falls back to checking RssSourceId and the Title.
        /// The entire operation is performed within a single database transaction to ensure atomicity.
        /// </summary>
        /// <param name="newsItem">The news item to add.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <summary>
        /// Adds a new NewsItem to the database if it does not already exist, using a single, atomic MERGE operation.
        /// This is an efficient "upsert" that prevents duplicate entries.
        /// <para>
        /// Duplicates are identified first by a combination of RssSourceId and a non-empty SourceItemId.
        /// If SourceItemId is unavailable, it falls back to checking RssSourceId and the Title.
        /// </para>
        /// </summary>
        /// <param name="newsItem">The news item to add.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task AddAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null)
            {
                throw new ArgumentNullException(nameof(newsItem));
            }

            _logger.LogDebug("Attempting to upsert NewsItem. Title: {Title}", newsItem.Title.Truncate(50));

            try
            {
                await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    await using SqlConnection connection = CreateConnection();

                    string mergeSql;
                    object mergeParams;
                    string identityColumn; // The column used for matching

                    // --- Step 1: Prepare the correct MERGE statement based on available data ---
                    if (!string.IsNullOrWhiteSpace(newsItem.SourceItemId))
                    {
                        identityColumn = "SourceItemId";
                        mergeParams = newsItem; // The whole object can be used as Dapper parameters
                        mergeSql = @"
MERGE INTO NewsItems AS Target
USING (SELECT @RssSourceId AS RssSourceId, @SourceItemId AS SourceItemId) AS Source
ON (Target.RssSourceId = Source.RssSourceId AND Target.SourceItemId = Source.SourceItemId)
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Id, Title, Link, Summary, FullContent, ImageUrl, PublishedDate, CreatedAt, LastProcessedAt, SourceName, SourceItemId, SentimentScore, SentimentLabel, DetectedLanguage, AffectedAssets, RssSourceId, IsVipOnly, AssociatedSignalCategoryId)
    VALUES (@Id, @Title, @Link, @Summary, @FullContent, @ImageUrl, @PublishedDate, @CreatedAt, @LastProcessedAt, @SourceName, @SourceItemId, @SentimentScore, @SentimentLabel, @DetectedLanguage, @AffectedAssets, @RssSourceId, @IsVipOnly, @AssociatedSignalCategoryId);
";
                    }
                    else
                    {
                        identityColumn = "Title";
                        mergeParams = newsItem;
                        mergeSql = @"
MERGE INTO NewsItems AS Target
USING (SELECT @RssSourceId AS RssSourceId, @Title AS Title) AS Source
ON (Target.RssSourceId = Source.RssSourceId AND Target.Title = Source.Title)
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Id, Title, Link, Summary, FullContent, ImageUrl, PublishedDate, CreatedAt, LastProcessedAt, SourceName, SourceItemId, SentimentScore, SentimentLabel, DetectedLanguage, AffectedAssets, RssSourceId, IsVipOnly, AssociatedSignalCategoryId)
    VALUES (@Id, @Title, @Link, @Summary, @FullContent, @ImageUrl, @PublishedDate, @CreatedAt, @LastProcessedAt, @SourceName, @SourceItemId, @SentimentScore, @SentimentLabel, @DetectedLanguage, @AffectedAssets, @RssSourceId, @IsVipOnly, @AssociatedSignalCategoryId);
";
                    }

                    // --- Step 2: Execute the atomic MERGE operation ---
                    CommandDefinition command = new(
                        mergeSql,
                        mergeParams,
                        commandTimeout: CommandTimeoutSeconds,
                        cancellationToken: ct);

                    // .ExecuteAsync returns the number of rows affected. 1 for an insert, 0 if it already existed.
                    int rowsAffected = await connection.ExecuteAsync(command).ConfigureAwait(false);

                    if (rowsAffected > 0)
                    {
                        _logger.LogInformation("Successfully inserted new NewsItem via MERGE. NewsItemId: {NewsItemId}", newsItem.Id);
                    }
                    else
                    {
                        _logger.LogInformation("Duplicate NewsItem found (matched by {IdentityColumn}). MERGE operation skipped insert. Title: {Title}", identityColumn, newsItem.Title.Truncate(50));
                    }

                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during MERGE operation for NewsItem {NewsItemId}. Title: {Title}", newsItem.Id, newsItem.Title.Truncate(50));
                throw new RepositoryException($"Failed to add or merge news item '{newsItem.Id}'.", ex);
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
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    string sql = @"
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
                    _ = await connection.ExecuteAsync(new CommandDefinition(sql, newsItems.Select(ni => new
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
                    }), commandTimeout: CommandTimeoutSeconds)); // <--- ADDED: Pass CommandTimeout
                });
                _logger.LogInformation("Successfully added {Count} news items.", newsItems.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding range of news items to the database.");
                throw new RepositoryException("Failed to add range of news items.", ex);
            }
        }

        /// <summary>
        /// Updates an existing NewsItem in the database.
        /// Throws an exception if the item with the specified ID is not found, indicating a potential concurrency issue.
        /// </summary>
        /// <param name="newsItem">The news item with updated values.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">Thrown if no rows are affected, which implies the item ID does not exist.</exception>
        /// <exception cref="RepositoryException">Thrown for general database or retry policy errors.</exception>
        public async Task UpdateAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null)
            {
                throw new ArgumentNullException(nameof(newsItem));
            }

            _logger.LogInformation("Updating NewsItem. NewsItemId: {NewsItemId}", newsItem.Id);

            try
            {
                await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    // Use 'await using' for modern async disposal
                    await using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(ct).ConfigureAwait(false);

                    string sql = @"
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

                    // ✅ UPGRADE: Pass the entire newsItem object directly.
                    // Dapper will map all the properties to the SQL parameters. This is cleaner.
                    CommandDefinition command = new(
                        sql,
                        newsItem, // <-- Cleaner parameter passing
                        commandTimeout: CommandTimeoutSeconds,
                        cancellationToken: ct);

                    int rowsAffected = await connection.ExecuteAsync(command).ConfigureAwait(false);

                    // ✅ UPGRADE: More precise concurrency check.
                    if (rowsAffected == 0)
                    {
                        // This is the most likely reason for 0 rows affected.
                        // It's a clear signal that the entity we tried to update doesn't exist.
                        throw new InvalidOperationException($"Update failed: NewsItem with ID '{newsItem.Id}' was not found in the database.");
                    }

                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Successfully updated NewsItem: {NewsItemId}", newsItem.Id);
            }
            catch (InvalidOperationException ex) // Catch our specific "not found" error
            {
                // This log is now more accurate.
                _logger.LogWarning(ex, "Update failed for NewsItem {NewsItemId}, likely because it was deleted before the update could be applied.", newsItem.Id);
                throw; // Re-throw to let the caller know the update failed.
            }
            catch (Exception ex) // Catch all other errors (DB connection, Polly timeout, etc.)
            {
                _logger.LogError(ex, "An unexpected error occurred while updating NewsItem {NewsItemId} in the database.", newsItem.Id);
                throw new RepositoryException($"Failed to update news item '{newsItem.Id}'.", ex);
            }
        }

        public async Task DeleteAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null)
            {
                throw new ArgumentNullException(nameof(newsItem));
            }

            _logger.LogInformation("Removing NewsItem. NewsItemId: {NewsItemId}", newsItem.Id);
            _ = await DeleteByIdAsync(newsItem.Id, cancellationToken); // Delegate to DeleteByIdAsync
        }

        public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to delete NewsItem by ID: {NewsItemId}", id);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using SqlConnection connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    // Note: If NewsItem has other entities that cascade delete, deleting the NewsItem will handle it.
                    // For performance, a simple DELETE is often best if cascades are set in DB.
                    string sql = "DELETE FROM NewsItems WHERE Id = @Id;";
                    int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, commandTimeout: CommandTimeoutSeconds)); // <--- ADDED: Pass CommandTimeout

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