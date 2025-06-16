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
        /// <summary>
        /// Represents a Data Transfer Object (DTO) for flattened data retrieved from the database,
        /// typically used for mapping results of SQL queries that perform joins between
        /// the `NewsItems` table and related tables like `RssSources` and `SignalCategories`.
        /// This DTO encapsulates a comprehensive view of a news item along with its source details
        /// and associated signal category information in a single, convenient object.
        /// </summary>
        /// <remarks>
        /// For the AI analysis program, this DTO is crucial as it provides the AI models
        /// (or subsequent processing stages) with a rich, contextualized view of each news item.
        /// It combines raw news content with metadata about its origin (RSS source) and its
        /// pre-classified category (signal category), including fields potentially populated
        /// by earlier AI stages like sentiment analysis.
        /// <br/><br/>
        /// This comprehensive data structure facilitates:
        /// <list type="bullet">
        ///     <item><description>
        ///         **AI Model Input:** Directly serves as input for AI models that need both news content
        ///         and its context (source, category) for further processing (e.g., more detailed sentiment analysis,
        ///         entity extraction, risk assessment based on source reliability).
        ///     </description></item>
        ///     <item><description>
        ///         **Reporting and Visualization:** Provides a ready-to-use object for generating reports,
        ///         populating dashboards, or presenting complete news item details to users.
        ///     </description></item>
        ///     <item><description>
        ///         **Feature Engineering:** Allows AI engineers to easily access various features (title, summary,
        ///         source properties, sentiment) from a single object for feature engineering in ML pipelines.
        ///     </description></item>
        ///     <item><description>
        ///         **Debugging and Auditing:** Captures a snapshot of the news item with all its relevant metadata
        ///         at the time of retrieval, aiding in debugging data flow and auditing AI decisions.
        ///     </description></item>
        /// </list>
        /// The aliasing convention (e.g., `RssSource_Id`, `AssociatedSignalCategory_Name`) is a common pattern
        /// used with micro-ORMs like Dapper to map joined columns to distinct properties in a flattened DTO.
        /// </remarks>
        private class NewsItemDbDto
        {
            /// <summary>
            /// The unique identifier of the news item.
            /// </summary>
            public Guid Id { get; set; }
            /// <summary>
            /// The title of the news item.
            /// </summary>
            public string Title { get; set; } = default!;
            /// <summary>
            /// The primary link (URL) to the full news article.
            /// </summary>
            public string Link { get; set; } = default!;
            /// <summary>
            /// A brief summary or description of the news item. Can be null.
            /// </summary>
            public string? Summary { get; set; }
            /// <summary>
            /// The full content of the news item. Can be null.
            /// </summary>
            public string? FullContent { get; set; }
            /// <summary>
            /// The URL of an image associated with the news item. Can be null.
            /// </summary>
            public string? ImageUrl { get; set; }
            /// <summary>
            /// The original publication date of the news item.
            /// </summary>
            public DateTime PublishedDate { get; set; }
            /// <summary>
            /// The date and time when the news item was first created/ingested into the system.
            /// </summary>
            public DateTime CreatedAt { get; set; }
            /// <summary>
            /// The date and time when the news item was last processed (e.g., by an AI sentiment analysis pipeline). Can be null.
            /// </summary>
            public DateTime? LastProcessedAt { get; set; }
            /// <summary>
            /// The name of the RSS source from which this news item originated. Can be null.
            /// </summary>
            public string? SourceName { get; set; }
            /// <summary>
            /// A unique identifier for the news item provided by its source (e.g., RSS item GUID). Can be null.
            /// </summary>
            public string? SourceItemId { get; set; }
            /// <summary>
            /// The sentiment score calculated for the news item (e.g., by an AI sentiment model). Can be null.
            /// </summary>
            public double? SentimentScore { get; set; }
            /// <summary>
            /// The sentiment label derived from the sentiment score (e.g., "Positive", "Negative", "Neutral"). Can be null.
            /// </summary>
            public string? SentimentLabel { get; set; }
            /// <summary>
            /// The detected language of the news item's content. Can be null.
            /// </summary>
            public string? DetectedLanguage { get; set; }
            /// <summary>
            /// A string representing any assets (e.g., financial instruments, companies) affected by the news. Can be null.
            /// </summary>
            public string? AffectedAssets { get; set; }
            /// <summary>
            /// The foreign key linking to the associated RSS source.
            /// </summary>
            public Guid RssSourceId { get; set; }
            /// <summary>
            /// Indicates if the news item is exclusive to VIP users.
            /// </summary>
            public bool IsVipOnly { get; set; }
            /// <summary>
            /// The foreign key linking to the associated signal category. Can be null.
            /// </summary>
            public Guid? AssociatedSignalCategoryId { get; set; }

            // Properties for RssSource (mapped using aliases for joined columns)
            /// <summary>
            /// The unique identifier of the associated RSS source (aliased from RssSource.Id).
            /// </summary>
            public Guid RssSource_Id { get; set; }
            /// <summary>
            /// The URL of the associated RSS source (aliased from RssSource.Url). Can be null.
            /// </summary>
            public string? RssSource_Url { get; set; }
            /// <summary>
            /// The name of the associated RSS source (aliased from RssSource.SourceName). Can be null.
            /// </summary>
            public string? RssSource_SourceName { get; set; }
            /// <summary>
            /// Indicates if the associated RSS source is active (aliased from RssSource.IsActive).
            /// </summary>
            public bool RssSource_IsActive { get; set; }
            /// <summary>
            /// The creation timestamp of the associated RSS source (aliased from RssSource.CreatedAt).
            /// </summary>
            public DateTime RssSource_CreatedAt { get; set; }
            /// <summary>
            /// The last update timestamp of the associated RSS source (aliased from RssSource.UpdatedAt). Can be null.
            /// </summary>
            public DateTime? RssSource_UpdatedAt { get; set; }
            /// <summary>
            /// The Last-Modified HTTP header of the associated RSS source's feed (aliased from RssSource.LastModifiedHeader). Can be null.
            /// </summary>
            public string? RssSource_LastModifiedHeader { get; set; }
            /// <summary>
            /// The ETag of the associated RSS source's feed (aliased from RssSource.ETag). Can be null.
            /// </summary>
            public string? RssSource_ETag { get; set; }
            /// <summary>
            /// The timestamp of the last fetch attempt for the associated RSS source (aliased from RssSource.LastFetchAttemptAt). Can be null.
            /// </summary>
            public DateTime? RssSource_LastFetchAttemptAt { get; set; }
            /// <summary>
            /// The timestamp of the last successful fetch for the associated RSS source (aliased from RssSource.LastSuccessfulFetchAt). Can be null.
            /// </summary>
            public DateTime? RssSource_LastSuccessfulFetchAt { get; set; }
            /// <summary>
            /// The configured fetch interval in minutes for the associated RSS source (aliased from RssSource.FetchIntervalMinutes). Can be null.
            /// </summary>
            public int? RssSource_FetchIntervalMinutes { get; set; }
            /// <summary>
            /// The count of consecutive fetch errors for the associated RSS source (aliased from RssSource.FetchErrorCount).
            /// </summary>
            public int RssSource_FetchErrorCount { get; set; }
            /// <summary>
            /// A description of the associated RSS source (aliased from RssSource.Description). Can be null.
            /// </summary>
            public string? RssSource_Description { get; set; }
            /// <summary>
            /// The default signal category ID associated with the RSS source (aliased from RssSource.DefaultSignalCategoryId). Can be null.
            /// </summary>
            public Guid? RssSource_DefaultSignalCategoryId { get; set; }


            // Properties for AssociatedSignalCategory (mapped using aliases for joined columns)
            /// <summary>
            /// The unique identifier of the associated signal category (aliased from SignalCategory.Id). Can be null.
            /// </summary>
            public Guid? AssociatedSignalCategory_Id { get; set; }
            /// <summary>
            /// The name of the associated signal category (aliased from SignalCategory.Name). Can be null.
            /// </summary>
            public string? AssociatedSignalCategory_Name { get; set; }
            /// <summary>
            /// A description of the associated signal category (aliased from SignalCategory.Description). Can be null.
            /// </summary>
            public string? AssociatedSignalCategory_Description { get; set; }
            /// <summary>
            /// Indicates if the associated signal category is active (aliased from SignalCategory.IsActive).
            /// </summary>
            public bool AssociatedSignalCategory_IsActive { get; set; }
            /// <summary>
            /// The sort order for the associated signal category (aliased from SignalCategory.SortOrder).
            /// </summary>
            public int AssociatedSignalCategory_SortOrder { get; set; }



            /// <summary>
            /// Converts the flattened `NewsItemDbDto` (Data Transfer Object from a database query)
            /// back into a rich domain entity, <see cref="NewsItem"/>. This method reconstructs
            /// the complex object graph, including associated <see cref="RssSource"/> and
            /// <see cref="SignalCategory"/> entities, based on the flattened properties.
            /// </summary>
            /// <returns>
            /// A fully populated <see cref="NewsItem"/> domain entity. If the joined properties
            /// for <see cref="RssSource"/> or <see cref="SignalCategory"/> were present in the DTO,
            /// their corresponding nested domain entities will also be instantiated and attached
            /// to the returned <see cref="NewsItem"/>. This enables the AI analysis program
            /// to work with a complete and context-rich representation of the news item.
            /// </returns>
            /// <remarks>
            /// For AI analysis and downstream processing, this conversion is vital because:
            /// <list type="bullet">
            ///     <item><description>
            ///         Domain Integrity: It restores the structured relationships between news items,
            ///         their sources, and their categories, which might be lost during flat database queries.
            ///     </description></item>
            ///     <item><description>
            ///         AI Feature Context: AI models often require context beyond the raw news text.
            ///         Having the `RssSource` details (e.g., `IsVipOnly`, `SourceName`) and `SignalCategory`
            ///         details (e.g., `Name`, `IsActive`) directly linked to the `NewsItem` entity
            ///         provides richer features for advanced AI analysis (e.g., source reliability assessment,
            ///         category-specific sentiment models).
            ///     </description></item>
            ///     <item><description>
            ///         Simplified Business Logic: Subsequent business logic and AI processing can
            ///         operate on a coherent domain model rather than disparate DTO properties, leading
            ///         to cleaner and more maintainable code.
            ///     </description></item>
            /// </list>
            /// This method assumes that if `RssSource_Id` or `AssociatedSignalCategory_Id` are populated,
            /// the other aliased properties for that related entity are also available from the query results.
            /// </remarks>
            public NewsItem ToDomainEntity()
            {
                // Initialize the core NewsItem properties directly from the DTO's flattened properties.
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

        /// <summary>
        /// Represents a Data Transfer Object (DTO) for mapping RSS source data retrieved from the database.
        /// This class facilitates the transfer of raw database query results for an RSS source
        /// into a structured object, which can then be converted into a domain entity.
        /// </summary>
        /// <remarks>
        /// This DTO is used internally within the data access layer (e.g., repositories)
        /// to represent rows from the `RssSources` table. It provides a clean, flat structure
        /// that directly mirrors the database schema for RSS source properties.
        /// For the AI analysis program, this DTO is an intermediate step in handling the metadata
        /// of the data sources themselves, which is crucial for monitoring feed health,
        /// managing ingestion schedules, and informing AI about source characteristics (e.g., `IsActive`, `FetchErrorCount`).
        private class RssSourceMapDto // Consider renaming to RssSourceDto for clarity
        {
            /// <summary>
            /// The unique identifier of the RSS source.
            /// </summary>
            public Guid Id { get; set; }
            /// <summary>
            /// The URL of the RSS feed.
            /// </summary>
            public string Url { get; set; } = default!;
            /// <summary>
            /// The human-readable name of the RSS source.
            /// </summary>
            public string SourceName { get; set; } = default!;
            /// <summary>
            /// Indicates if the RSS source is currently active for fetching.
            /// </summary>
            public bool IsActive { get; set; }
            /// <summary>
            /// The date and time when the RSS source record was created.
            /// </summary>
            public DateTime CreatedAt { get; set; }
            /// <summary>
            /// The date and time when the RSS source record was last updated. Can be null.
            /// </summary>
            public DateTime? UpdatedAt { get; set; }
            /// <summary>
            /// The value of the 'Last-Modified' HTTP header from the last successful fetch. Can be null.
            /// </summary>
            public string? LastModifiedHeader { get; set; }
            /// <summary>
            /// The value of the 'ETag' HTTP header from the last successful fetch. Can be null.
            /// </summary>
            public string? ETag { get; set; }
            /// <summary>
            /// The date and time of the last attempt to fetch this RSS feed. Can be null.
            /// </summary>
            public DateTime? LastFetchAttemptAt { get; set; }
            /// <summary>
            /// The date and time of the last successful fetch of this RSS feed. Can be null.
            /// </summary>
            public DateTime? LastSuccessfulFetchAt { get; set; }
            /// <summary>
            /// The configured interval (in minutes) between fetches for this RSS source. Can be null.
            /// </summary>
            public int? FetchIntervalMinutes { get; set; }
            /// <summary>
            /// The count of consecutive errors encountered during fetching this RSS source.
            /// </summary>
            public int FetchErrorCount { get; set; }
            /// <summary>
            /// A description of the RSS source. Can be null.
            /// </summary>
            public string? Description { get; set; }
            /// <summary>
            /// The ID of the default signal category associated with news items from this source. Can be null.
            /// </summary>
            public Guid? DefaultSignalCategoryId { get; set; }

            /// <summary>
            /// Converts this `RssSourceMapDto` instance into its corresponding domain entity, <see cref="RssSource"/>.
            /// This method performs a direct mapping of properties from the DTO to the domain entity.
            /// </summary>
            /// <returns>
            /// A fully populated <see cref="RssSource"/> domain entity, representing the details
            /// of an RSS feed source within the application's domain model. This entity is then
            /// used by the AI analysis program for various operations, such as determining which
            /// feeds to fetch and how to process them.
            /// </returns>
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

        /// <summary>
        /// Represents a Data Transfer Object (DTO) for mapping `SignalCategory` data retrieved from the database.
        /// This class provides a flat structure that mirrors the `SignalCategories` table schema,
        /// facilitating the transfer of category-related information between the database and the application's domain layer.
        /// </summary>
        /// <remarks>
        /// For the AI analysis program, this DTO is fundamental. Signal categories are crucial for:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Content Classification:** Categorizing news items or signals, allowing AI models to focus on relevant data streams.
        ///     </description></item>
        ///     <item><description>
        ///         **User Filtering:** Enabling users to subscribe to specific categories of AI-generated signals or news.
        ///     </description></item>
        ///     <item><description>
        ///         **AI Model Training:** Providing labels for supervised learning models that classify news content.
        ///     </description></item>
        ///     <item><description>
        ///         **Reporting and Analysis:** Structuring data for reports on signal distribution and user engagement per category.
        ///     </description></item>
        /// </list>
        /// </remarks>
        private class SignalCategoryMapDto
        {
            /// <summary>
            /// The unique identifier of the signal category.
            /// </summary>
            public Guid Id { get; set; }
            /// <summary>
            /// The name of the signal category (e.g., "Market News", "Geopolitical Events").
            /// </summary>
            public string Name { get; set; } = default!;
            /// <summary>
            /// A detailed description of the signal category. Can be null.
            /// </summary>
            public string? Description { get; set; }
            /// <summary>
            /// Indicates if the signal category is currently active. Inactive categories might not be used for new signals.
            /// </summary>
            public bool IsActive { get; set; }
            /// <summary>
            /// The numerical order used for sorting and displaying categories.
            /// </summary>
            public int SortOrder { get; set; }

            /// <summary>
            /// Converts this `SignalCategoryMapDto` instance into its corresponding domain entity, <see cref="SignalCategory"/>.
            /// This method performs a direct mapping of properties from the DTO to the domain entity.
            /// </summary>
            /// <returns>
            /// A fully populated <see cref="SignalCategory"/> domain entity.
            /// This domain entity represents a specific category for signals or news within the application's
            /// core business logic, providing rich context for AI analysis and downstream processing.
            /// </returns>
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



        /// <summary>
        /// Initializes a new instance of the <see cref="NewsItemRepository"/> class.
        /// This constructor sets up the database connection string and configures a robust
        /// Polly retry policy to handle transient database errors, ensuring data persistence
        /// operations for news items are resilient.
        /// </summary>
        /// <param name="configuration">The application's configuration, used to retrieve the database connection string.</param>
        /// <param name="logger">The logger instance for recording operational events and errors within the repository.</param>
        /// <returns>
        /// A new instance of <see cref="NewsItemRepository"/>, ready to perform data access operations
        /// for <see cref="NewsItem"/> entities with built-in resilience.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="logger"/> or <paramref name="configuration"/> is <c>null</c>,
        /// or if the "DefaultConnection" connection string is not found in the configuration.
        /// </exception>
        /// <remarks>
        /// **Role for AI Analysis and MLOps:**
        /// This constructor's setup is vital for the reliability of the AI analysis program's data layer:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Data Integrity (Input/Output for AI):** This repository is where raw news items are stored
        ///         after ingestion and where AI-processed results might be updated. A robust persistence layer
        ///         ensures that the data AI consumes and produces is reliably stored.
        ///     </description></item>
        ///     <item><description>
        ///         **Resilience:** The Polly retry policy specifically targets transient database errors.
        ///         This means temporary network glitches, database server restarts, or momentary overloads
        ///         will not immediately cause AI analysis pipelines to fail due to data access issues.
        ///         This significantly improves the overall stability of an MLOps system.
        ///     </description></item>
        ///     <item><description>
        ///         **Error Handling:** The retry policy is configured to *not* retry on primary key or unique
        ///         constraint violations (SQL Server error codes 2627, 2601). This is a smart design choice
        ///         for AI systems because:
        ///         <list type="circle">
        ///             <item><description>
        ///                 It avoids useless retries on fundamentally bad data (e.g., trying to insert a duplicate news item).
        ///             </description></item>
        ///             <item><description>
        ///                 It allows these specific errors to bubble up immediately, indicating potential issues
        ///                 in the data ingestion/deduplication logic that AI might need to be aware of or adapt to.
        ///             </description></item>
        ///         </list>
        ///     </description></item>
        ///     <item><description>
        ///         **Observability (MLOps):** The `onRetry` callback provides crucial logs detailing transient
        ///         database errors and retry attempts. MLOps teams can monitor these logs to:
        ///         <list type="circle">
        ///             <item><description>Assess the health and performance of the database backing the AI system.</description></item>
        ///             <item><description>Identify patterns of intermittent database issues that might impact AI training data or inference results.</description></item>
        ///         </list>
        ///     </description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Creates and returns a new instance of <see cref="SqlConnection"/> using the configured connection string.
        /// This method is a private helper that encapsulates the connection creation logic, ensuring that
        /// all data access operations within the repository use the correct connection configuration.
        /// </summary>
        /// <returns>
        /// A new, un-opened instance of <see cref="SqlConnection"/> that is configured to connect
        /// to the database specified by the `_connectionString`.
        /// </returns>
        /// <remarks>
        /// For the AI analysis program, consistently creating connections via this method is important for:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Resource Management:** In a typical ASP.NET Core application, ADO.NET connections
        ///         (like <see cref="SqlConnection"/>) are automatically pooled. Calling `new SqlConnection()`
        ///         doesn't create a new physical connection every time but rather retrieves one from the pool.
        ///         This optimizes database resource usage.
        ///     </description></item>
        ///     <item><description>
        ///         **Reliability:** By ensuring each data operation gets a fresh connection object (even if pooled),
        ///         it helps avoid issues with stale or misused connections, contributing to the overall
        ///         reliability of the data pipeline for AI.
        ///     </description></item>
        ///     <item><description>
        ///         **Testability and Maintenance:** Centralizing connection creation makes it easier to
        ///         manage and potentially mock in tests, or update connection logic in one place.
        ///     </description></item>
        /// </list>
        /// Consumers of this method (e.g., repository methods) are responsible for properly opening,
        /// using, and disposing of the returned <see cref="SqlConnection"/> instance (typically via `using` statements)
        /// to ensure connections are returned to the pool efficiently.
        /// </remarks>
        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        #region SearchNewsAsync Implementation


        /// <summary>
        /// Searches for news items within the database based on specified keywords, a date range, and pagination criteria.
        /// This method provides a flexible way to retrieve news content, filtering by publication date,
        /// keyword presence (in title or summary), and user VIP status. It uses SQL `LIKE` operator for keyword matching,
        /// making it a versatile but potentially slower fallback for full-text search.
        /// </summary>
        /// <param name="keywords">A collection of strings representing keywords to search for in news item titles and summaries. Case-insensitive.</param>
        /// <param name="sinceDate">The start date (inclusive) for the news item's publication date filter.</param>
        /// <param name="untilDate">The end date (inclusive) for the news item's publication date filter.</param>
        /// <param name="pageNumber">The desired page number for pagination. Must be greater than 0. Defaults to 1 if invalid.</param>
        /// <param name="pageSize">The number of news items per page. Must be greater than 0. Defaults to 10 if invalid.</param>
        /// <param name="matchAllKeywords">
        /// A boolean indicating the keyword matching logic:
        /// <list type="bullet">
        ///     <item><description><c>true</c>: All provided keywords must be present (AND logic).</description></item>
        ///     <item><description><c>false</c>: At least one of the provided keywords must be present (OR logic).</description></item>
        /// </list>
        /// </param>
        /// <param name="isUserVip">A boolean indicating if the requesting user is VIP. If <c>false</c>, only non-VIP news items will be returned.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the database operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous search operation. The task resolves to a tuple:
        /// <list type="bullet">
        ///     <item><term>Items</term><description>A <see cref="List{NewsItem}"/> containing the paged news items that match the search criteria. This list includes reconstructed domain entities with their associated RSS source and signal category data.</description></item>
        ///     <item><term>TotalCount</term><description>An <see cref="int"/> representing the total count of news items that match all search criteria, irrespective of pagination.</description></item>
        /// </list>
        /// The list of items will be empty and `TotalCount` will be 0 if no matching news items are found.
        /// </returns>
        /// <exception cref="RepositoryException">Thrown if an error occurs during the database interaction after exhausting retry attempts, wrapping the original exception.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is signaled during the database operation.</exception>
        /// <remarks>
        /// For AI analysis: This search functionality is critical for providing curated data sets. AI models might use this to:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Retrieve Training Data:** Fetch specific news items for model training or retraining based on dates, categories, or keywords.
        ///     </description></item>
        ///     <item><description>
        ///         **Validate AI Output:** Query for news items (e.g., within a specific date range) to compare AI-generated signals against the underlying news content.
        ///     </description></item>
        ///     <item><description>
        ///         **Ad-hoc Analysis:** Support exploratory data analysis by AI engineers or data scientists, allowing them to filter news by criteria relevant to their research.
        ///     </description></item>
        ///     <item><description>
        ///         **User Interaction Insights:** Understand what kind of news users are searching for (via UI, if exposed), which can inform future AI content generation.
        ///     </description></item>
        /// </list>
        /// The `LIKE` based keyword search, while functional, implies that for very large datasets, performance might be a concern,
        /// and dedicated full-text search solutions (e.g., SQL Server Full-Text Search, Elasticsearch) might be considered for future AI-driven applications that require high-performance content retrieval.
        /// </remarks>
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
                    using var connection = CreateConnection();
                    await connection.OpenAsync(ct);

                    var whereClauses = new List<string>();
                    var parameters = new DynamicParameters();

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
                    var keywordList = keywords?.Select(k => k.Trim()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                    if (keywordList != null && keywordList.Any())
                    {
                        var keywordConditions = new List<string>();
                        for (int i = 0; i < keywordList.Count; i++)
                        {
                            var keyword = keywordList[i];
                            var paramName = $"keyword{i}";
                            // We search in Title and Summary. Searching in FullContent can be very slow.
                            keywordConditions.Add($"(LOWER(n.Title) LIKE '%' + LOWER(@{paramName}) + '%' OR LOWER(n.Summary) LIKE '%' + LOWER(@{paramName}) + '%')");
                            parameters.Add(paramName, keyword);
                        }

                        string keywordOperator = matchAllKeywords ? " AND " : " OR ";
                        whereClauses.Add($"({string.Join(keywordOperator, keywordConditions)})");
                    }

                    var fullWhereClause = whereClauses.Any() ? "WHERE " + string.Join(" AND ", whereClauses) : "";

                    // --- Back to Two Separate Queries ---
                    // This is necessary because QueryMultiple + Multi-mapping is problematic in Dapper.

                    // Query 1: Get the total count
                    var countSql = $"SELECT COUNT(n.Id) FROM NewsItems n {fullWhereClause};";
                    var totalCount = await connection.ExecuteScalarAsync<int>(
                        new CommandDefinition(countSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)
                    );

                    if (totalCount == 0)
                    {
                        return (new List<NewsItem>(), 0);
                    }

                    // Query 2: Get the paged data
                    var sql = $@"
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
                    var newsItemsMap = new Dictionary<Guid, NewsItem>();
                    var items = await connection.QueryAsync<NewsItemDbDto, RssSourceMapDto, SignalCategoryMapDto, NewsItem>(
                        new CommandDefinition(sql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct),
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            if (!newsItemsMap.TryGetValue(newsItemDto.Id, out var newsItem))
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

        /// <summary>
        /// Asynchronously retrieves a single <see cref="NewsItem"/> domain entity from the database
        /// by its unique identifier. This method performs a comprehensive lookup, including
        /// joining related data from `RssSources` and `SignalCategories` tables, to provide
        /// a fully contextualized news item object.
        /// </summary>
        /// <param name="id">The unique identifier (<see cref="Guid"/>) of the <see cref="NewsItem"/> to retrieve.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the database operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous retrieval operation. The task resolves to:
        /// <list type="bullet">
        ///     <item><description>
        ///         A <see cref="NewsItem"/> domain entity if a matching news item is found. This entity
        ///         will have its `RssSource` and `AssociatedSignalCategory` navigation properties
        ///         (if present in the database) automatically populated from the joined data.
        ///     </description></item>
        ///     <item><description>
        ///         <c>null</c> if no news item with the specified <paramref name="id"/> is found in the database.
        ///     </description></item>
        /// </list>
        /// </returns>
        /// <exception cref="RepositoryException">
        /// Thrown if an error occurs during the database interaction (e.g., connection issues, SQL execution errors)
        /// after exhausting all configured retry attempts. This custom exception wraps the original underlying exception.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="cancellationToken"/> is signaled while the database operation is in progress
        /// (e.g., during connection opening or query execution).
        /// </exception>
        /// <remarks>
        /// For AI analysis: This method is vital for providing detailed context for individual news items.
        /// AI models or downstream processes might use this to:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Drill-down Analysis:** When an AI flags a specific news item, this method can fetch its full context
        ///         for human review or more intensive secondary AI analysis.
        ///     </description></item>
        ///     <item><description>
        ///         **Re-analysis/Verification:** Retrieve specific news items for re-processing with updated AI models
        ///         or for verifying previous AI classifications.
        ///     </description></item>
        ///     <item><description>
        ///         **Data Enrichment:** Provides a rich data structure that can be further enriched by other AI components
        ///         (e.g., entity extraction, event detection) before being presented to users.
        ///     </description></item>
        /// </list>
        /// The method leverages Dapper's multi-mapping feature to efficiently populate a flattened DTO (`NewsItemDbDto`)
        /// from the SQL query's joined results and then converts it into the rich domain entity graph.
        /// It is protected by a Polly retry policy (`_retryPolicy`) to ensure resilience against transient database issues.
        /// </remarks>
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
                        new CommandDefinition(sql, new { Id = id }, commandTimeout: CommandTimeoutSeconds), // <--- ADDED: Pass CommandTimeout
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            var item = newsItemDto.ToDomainEntity();
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


        /// <summary>
        /// Asynchronously retrieves a single <see cref="NewsItem"/> domain entity from the database
        /// using its RSS source ID and the source-specific item ID. This method is crucial for
        /// deduplication and efficient lookup of news items that originate from a specific RSS feed.
        /// It performs a database query that joins the `NewsItems` table with `RssSources` and `SignalCategories`
        /// to fetch all related information in a single roundtrip, then maps the flattened data into a rich domain entity.
        /// </summary>
        /// <param name="rssSourceId">The unique identifier (<see cref="Guid"/>) of the RSS source to which the news item belongs.</param>
        /// <param name="sourceItemId">The unique identifier of the news item as provided by its originating RSS feed (e.g., GUID or permalink hash).</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the database operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous operation. The task resolves to:
        /// <list type="bullet">
        ///     <item><description>The <see cref="NewsItem"/> domain entity if a matching item is found with the specified source details, with its associated <see cref="RssSource"/> and <see cref="SignalCategory"/> navigation properties populated.</description></item>
        ///     <item><description><c>null</c> if <paramref name="sourceItemId"/> is null or whitespace, or if no news item with the specified source details is found in the database.</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="RepositoryException">Thrown if an error occurs during the database interaction after exhausting retry attempts, wrapping the original exception.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is signaled during the database operation.</exception>
        /// <remarks>
        /// For AI analysis: This method is a primary mechanism for the deduplication process in the RSS ingestion pipeline.
        /// AI models that analyze incoming news feeds rely on this capability to ensure that already processed
        /// or stored news items are not re-ingested or re-analyzed, maintaining data integrity and efficiency.
        /// <br/><br/>
        /// Key aspects related to AI:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Deduplication:** Directly supports the `IRssReaderService` in preventing duplicate news items from
        ///         entering the system, which would skew AI analysis results and waste processing resources.
        ///     </description></item>
        ///     <item><description>
        ///         **Data Consistency:** Ensures that a unique `(RssSourceId, SourceItemId)` pair maps to a single
        ///         `NewsItem` record, which is fundamental for reliable AI training and inference.
        ///     </description></item>
        ///     <item><description>
        ///         **Performance:** The query is indexed on these fields for fast lookups, critical in high-volume
        ///         ingestion scenarios where AI consumes data continuously.
        ///     </description></item>
        /// </list>
        /// </remarks>
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
                        new CommandDefinition(sql, new { RssSourceId = rssSourceId, SourceItemId = sourceItemId }, commandTimeout: CommandTimeoutSeconds), // <--- ADDED: Pass CommandTimeout
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            var item = newsItemDto.ToDomainEntity();
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


        /// <summary>
        /// Asynchronously checks if a news item with specific source details (RSS source ID and source-provided item ID)
        /// already exists in the database. This method is a critical component for preventing duplicate news items
        /// from being ingested and processed, ensuring data integrity within the AI analysis pipeline.
        /// </summary>
        /// <param name="rssSourceId">The unique identifier (Guid) of the RSS source to which the news item belongs.</param>
        /// <param name="sourceItemId">The unique identifier of the news item as provided by its originating RSS feed (e.g., GUID, permalink, or a hash derived from it).</param>
        /// <param name="cancellationToken">A CancellationToken to observe for cancellation requests during the database operation.</param>
        /// <returns>
        /// A <see cref="Task{bool}"/> that represents the asynchronous operation. The task resolves to:
        /// <list type="bullet">
        ///     <item><description><c>true</c> if a news item matching both <paramref name="rssSourceId"/> and <paramref name="sourceItemId"/> is found in the database.</description></item>
        ///     <item><description><c>false</c> if no such news item exists, or if <paramref name="sourceItemId"/> is null or whitespace.</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="RepositoryException">Thrown if an error occurs during the database interaction after exhausting retry attempts, wrapping the original exception.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is signaled during the database operation.</exception>
        /// <remarks>
        /// For AI analysis: This method is fundamental for the data ingestion and preprocessing stages.
        /// <list type="bullet">
        ///     <item><description>
        ///         **Deduplication:** It serves as the primary mechanism to ensure that our AI analysis models
        ///         operate on unique news items. Duplicate data can skew training outcomes, waste computational
        ///         resources during inference, and lead to redundant notifications to users.
        ///     </description></item>
        ///     <item><description>
        ///         **Data Quality:** By preventing re-ingestion of identical content, it maintains a clean and
        ///         accurate dataset for AI feature engineering and model training.
        ///     </description></item>
        ///     <item><description>
        ///         **Efficiency:** The database query for existence (`COUNT(*)`) is typically very fast, especially
        ///         when indexed on `RssSourceId` and `SourceItemId`, making this a performant check in high-volume
        ///         data ingestion scenarios essential for real-time AI systems.
        ///     </description></item>
        ///     <item><description>
        ///         **Resilience:** The operation is wrapped in a Polly retry policy (`_retryPolicy`) to handle
        ///         transient database connectivity issues, ensuring that temporary problems do not halt the
        ///         overall data ingestion pipeline that feeds AI.
        ///     </description></item>
        /// </list>
        /// </remarks>
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
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var sql = "SELECT COUNT(*) FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId = @SourceItemId;";
                    var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { RssSourceId = rssSourceId, SourceItemId = sourceItemId }, commandTimeout: CommandTimeoutSeconds)); // <--- ADDED: Pass CommandTimeout
                    return count > 0;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking existence of NewsItem by source details RssSourceId: {RssSourceId}, SourceItemId: {SourceItemId}.", rssSourceId, sourceItemId);
                throw new RepositoryException($"Failed to check existence of news item by source details '{rssSourceId}', '{sourceItemId}'.", ex);
            }
        }


        /// <summary>
        /// Asynchronously retrieves a specified number of the most recent news items from the database.
        /// This method allows for optional filtering by a specific RSS source and ensures that
        /// related entities (like the RSS source itself and its associated signal category) are
        /// eagerly loaded to provide a complete domain model for each news item.
        /// </summary>
        /// <param name="count">The maximum number of recent news items to retrieve. If less than or equal to 0, an empty collection is returned.</param>
        /// <param name="rssSourceId">
        /// An optional unique identifier (Guid) of an RSS source. If provided, only news items from this specific source will be returned.
        /// If null, news items from all sources will be considered.
        /// </param>
        /// <param name="includeRssSource">
        /// This parameter is retained for interface compatibility but is less relevant for Dapper.
        /// The implementation implicitly includes RSS source and signal category data via SQL JOINs and multi-mapping
        /// when mapping to <see cref="NewsItemDbDto"/> and then to the <see cref="NewsItem"/> domain entity.
        /// </param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the database operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous operation. The task resolves to an <see cref="IEnumerable{NewsItem}"/>:
        /// <list type="bullet">
        ///     <item><description>A collection of <see cref="NewsItem"/> domain entities, ordered by publication date (descending) and then creation date (descending), representing the most recent matching news items up to the specified `count`.</description></item>
        ///     <item><description>Each <see cref="NewsItem"/> in the collection will have its `RssSource` and `AssociatedSignalCategory` navigation properties populated if the corresponding data exists in the database.</description></item>
        ///     <item><description>An empty collection if `count` is less than or equal to 0, or if no news items match the criteria.</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="RepositoryException">Thrown if an error occurs during the database interaction after exhausting retry attempts, wrapping the original exception.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is signaled during the database operation.</exception>
        /// <remarks>
        /// For AI analysis: This method is fundamental for providing fresh, relevant data to AI models. It enables:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Real-time Data Feeds:** Providing the latest news items for AI models that perform continuous or near real-time analysis (e.g., sentiment analysis, event detection).
        ///     </description></item>
        ///     <item><description>
        ///         **Model Monitoring:** Fetching recent news to evaluate how AI models are performing on new, unseen data, or to identify concept drift.
        ///     </description></item>
        ///     <item><description>
        ///         **Dashboards and Reporting:** Populating UI elements or reports with the latest AI-relevant news, giving human analysts an overview of current events.
        ///     </description></item>
        ///     <item><description>
        ///         **Targeted Data Acquisition:** The `rssSourceId` filter allows AI models to focus on specific data streams relevant to their domain.
        ///     </description></item>
        /// </list>
        /// The query is designed for efficiency by using `OFFSET 0 ROWS FETCH NEXT @Count ROWS ONLY` and eager loading related entities with Dapper's multi-mapping.
        /// </remarks>
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
                        new CommandDefinition(sql, parameters, commandTimeout: CommandTimeoutSeconds), // <--- ADDED: Pass CommandTimeout
                        (newsItemDto, rssSourceDto, signalCategoryDto) =>
                        {
                            var item = newsItemDto.ToDomainEntity();
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


        /// <summary>
        /// This method is part of the <see cref="INewsItemRepository"/> interface contract,
        /// but it is explicitly **NOT SUPPORTED** by this Dapper-based implementation.
        /// It is a placeholder designed to immediately throw a <see cref="NotSupportedException"/>
        /// if called, signaling that arbitrary LINQ <see cref="Expression"/> predicates cannot be
        /// directly translated to SQL by this micro-ORM.
        /// </summary>
        /// <remarks>
        /// In the context of our AI analysis program, this method highlights a design decision:
        /// choosing Dapper for performance and granular control over SQL, rather than a full ORM
        /// (like Entity Framework) that provides automatic LINQ-to-SQL translation.
        /// <br/><br/>
        /// **Implications for AI Analysis and MLOps:**
        /// <list type="bullet">
        ///     <item><description>
        ///         **Data Access Strategy:** AI engineers and data scientists using this system
        ///         must be aware that complex data filtering for training or inference cannot
        ///         be done via arbitrary LINQ expressions against the repository. They must
        ///         rely on pre-defined, explicit SQL-based query methods (e.g., `SearchNewsAsync`)
        ///         or contribute new, specific query methods to the repository.
        ///     </description></item>
        ///     <item><description>
        ///         **Pipeline Development:** When building data pipelines for AI, attempting to
        ///         use this method for dynamic data selection will lead to runtime failures.
        ///         This limitation needs to be communicated clearly in development guidelines.
        ///     </description></item>
        ///     <item><description>
        ///         **Maintainability:** While it limits flexibility, this approach ensures
        ///         that SQL queries are explicit and optimized, which can be beneficial
        ///         for performance-critical AI workloads.
        ///     </description></item>
        ///     <item><description>
        ///         **Error Clarity:** The explicit `NotSupportedException` provides immediate,
        ///         clear feedback that the intended usage pattern is not implemented,
        ///         preventing silent failures or unexpected behavior in AI data retrieval.
        ///     </description></item>
        /// </list>
        /// </remarks>
        /// <param name="predicate">
        /// An <see cref="Expression{TDelegate}"/> representing the LINQ predicate to apply.
        /// This parameter is not used in the implementation and will cause a <see cref="NotSupportedException"/>.
        /// </param>
        /// <param name="includeRssSource">
        /// A boolean indicating whether to include the associated RSS source. This parameter is also
        /// not used by this non-functional implementation.
        /// </param>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe for cancellation requests. This parameter is
        /// not actively used as the method immediately throws an exception.
        /// </param>
        /// <returns>
        /// This method is declared to return a <see cref="Task{TResult}"/> of <see cref="IEnumerable{NewsItem}"/>
        /// to satisfy the interface, but it will *never* return a value.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// This exception is explicitly thrown every time this method is called, as arbitrary LINQ
        /// expression predicates are not supported by this Dapper repository implementation.
        /// </exception>
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


        /// <summary>
        /// Asynchronously fetches a set of unique `SourceItemId` values from the database
        /// for a specific RSS source. This method is crucial for efficiently determining
        /// which incoming news items from an RSS feed have already been processed and stored,
        /// thereby preventing duplicates in the database.
        /// </summary>
        /// <param name="rssSourceId">The unique identifier (<see cref="Guid"/>) of the RSS source
        /// for which to retrieve the existing source item IDs.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests
        /// during the database query operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous operation. The task resolves to a <see cref="HashSet{string}"/>:
        /// <list type="bullet">
        ///     <item><description>
        ///         A <see cref="HashSet{string}"/> containing all `SourceItemId` values associated
        ///         with the given `rssSourceId` that are currently stored in the `NewsItems` table.
        ///         The <see cref="HashSet{T}"/> is configured for case-insensitive comparisons
        ///         (<see cref="StringComparer.OrdinalIgnoreCase"/>) to ensure accurate duplicate detection.
        ///     </description></item>
        ///     <item><description>
        ///         An empty <see cref="HashSet{string}"/> if no existing items are found for the specified source.
        ///     </description></item>
        /// </list>
        /// </returns>
        /// <exception cref="RepositoryException">Thrown if an error occurs during the database interaction after exhausting retry attempts, wrapping the original exception.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is signaled during the database operation.</exception>
        /// <remarks>
        /// For AI analysis: This method is a core component of the data ingestion pipeline, directly supporting:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Deduplication for AI Training Data:** Ensures that duplicate news articles are not
        ///         repeatedly fed into AI models for training or inference, which can lead to
        ///         biased models or wasted computational resources.
        ///     </description></item>
        ///     <item><description>
        ///         **Data Quality:** Maintains the cleanliness and uniqueness of the dataset that
        ///         the AI operates on, crucial for reliable analytical outcomes.
        ///     </description></item>
        ///     <item><description>
        ///         **Efficiency:** Using a `HashSet` allows for very fast (average O(1)) lookups
        ///         when checking if a newly fetched RSS item has already been processed, which is
        ///         critical in high-volume ingestion scenarios for AI.
        ///     </description></item>
        ///     <item><description>
        ///         **Resilience:** The operation is wrapped in a Polly retry policy (`_retryPolicy`)
        ///         to handle transient database connectivity issues, contributing to the overall
        ///         robustness of the data pipeline for AI.
        ///     </description></item>
        /// </list>
        /// </remarks>
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
                    var ids = (await connection.QueryAsync<string>(new CommandDefinition(sql, new { RssSourceId = rssSourceId }, commandTimeout: CommandTimeoutSeconds))).ToList(); // <--- ADDED: Pass CommandTimeout
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
                    await using var connection = CreateConnection();

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
                    var command = new CommandDefinition(
                        mergeSql,
                        mergeParams,
                        commandTimeout: CommandTimeoutSeconds,
                        cancellationToken: ct);

                    // .ExecuteAsync returns the number of rows affected. 1 for an insert, 0 if it already existed.
                    var rowsAffected = await connection.ExecuteAsync(command).ConfigureAwait(false);

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
                    await using var connection = CreateConnection();
                    await connection.OpenAsync(ct).ConfigureAwait(false);

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

                    // ✅ UPGRADE: Pass the entire newsItem object directly.
                    // Dapper will map all the properties to the SQL parameters. This is cleaner.
                    var command = new CommandDefinition(
                        sql,
                        newsItem, // <-- Cleaner parameter passing
                        commandTimeout: CommandTimeoutSeconds,
                        cancellationToken: ct);

                    var rowsAffected = await connection.ExecuteAsync(command).ConfigureAwait(false);

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
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    // Note: If NewsItem has other entities that cascade delete, deleting the NewsItem will handle it.
                    // For performance, a simple DELETE is often best if cascades are set in DB.
                    var sql = "DELETE FROM NewsItems WHERE Id = @Id;";
                    var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, commandTimeout: CommandTimeoutSeconds)); // <--- ADDED: Pass CommandTimeout

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