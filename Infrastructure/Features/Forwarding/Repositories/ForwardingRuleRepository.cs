// // File: Infrastructure/Features/Forwarding/Repositories/ForwardingRuleRepository.cs

#region Usings
// Standard .NET & NuGet
using Dapper; // Added for Dapper
// Project specific
using Domain.Features.Forwarding.Entities;     // For ForwardingRule entity
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository interface
using Domain.Features.Forwarding.ValueObjects; // For MessageEditOptions, MessageFilterOptions, TextReplacement
using Microsoft.Data.SqlClient; // Added for SqlConnection (assuming SQL Server)
// using Infrastructure.Data; // No longer directly using AppDbContext here
using Microsoft.EntityFrameworkCore; // Still needed for DbUpdateConcurrencyException type check in Polly
using Microsoft.Extensions.Configuration; // Added to get connection string
using Microsoft.Extensions.Logging;   // For logging
using Polly; // For Polly policies
using Polly.Retry; // For Retry policy
using System.Data; // For IDbConnection
using System.Data.Common; // For DbException (base class for database exceptions)
using System.Text.Json; // For JSON serialization/deserialization
using System.Text.RegularExpressions; // For RegexOptions conversion
#endregion

namespace Infrastructure.Features.Forwarding.Repositories
{
    /// <summary>
    /// Implementation of the repository for managing forwarding rules (ForwardingRule) using Dapper.
    /// This class provides CRUD operations for the ForwardingRule entity and uses Polly
    /// for increased resilience against transient database errors.
    /// </summary>
    public class ForwardingRuleRepository : IForwardingRuleRepository
    {
        private readonly string _connectionString; // Changed from AppDbContext to connection string
        private readonly ILogger<ForwardingRuleRepository> _logger;
        private readonly AsyncRetryPolicy _retryPolicy;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>
        /// Initializes a new instance of the ForwardingRuleRepository class.
        /// </summary>
        /// <param name="configuration">The application's configuration, used to retrieve the database connection string.</param>
        /// <param name="logger">The logger for logging information and errors.</param>
        public ForwardingRuleRepository(
              IConfiguration configuration, // Injected IConfiguration
              ILogger<ForwardingRuleRepository> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") // Assuming "DefaultConnection" is used
                                ?? throw new ArgumentNullException("DefaultConnection", "DefaultConnection string not found in configuration.");
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Initialize the Polly policy for retrying transient database errors.
            // This policy handles any DbException (like SqlException, NpgsqlException, etc.),
            _retryPolicy = Policy
               .Handle<DbException>(ex => ex.GetType() != typeof(DbUpdateConcurrencyException)) // Handles database errors, except concurrency errors  
               .WaitAndRetryAsync(
                   retryCount: 3, // Maximum 3 retries  
                   sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff: 2s, 4s, 8s  
                   onRetry: (exception, timeSpan, retryAttempt, context) =>
                   {
                       _logger.LogWarning(exception,
                           "ForwardingRuleRepository: Transient database error encountered. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                           timeSpan, retryAttempt, exception.Message);
                   });
        }
        #region Internal DTOs for Dapper Mapping

        // DTO that mirrors the flattened database structure for ForwardingRule
        private class ForwardingRuleDbDto
        {
            public string RuleName { get; set; } = default!;
            public bool IsEnabled { get; set; }
            public long SourceChannelId { get; set; }
            public string TargetChannelIds { get; set; } = default!; // JSON string

            // Flattened properties from MessageEditOptions
            public string? EditOptions_PrependText { get; set; }
            public string? EditOptions_AppendText { get; set; }
            public bool EditOptions_RemoveSourceForwardHeader { get; set; }
            public bool EditOptions_RemoveLinks { get; set; }
            public bool EditOptions_StripFormatting { get; set; }
            public string? EditOptions_CustomFooter { get; set; }
            public bool EditOptions_DropAuthor { get; set; }
            public bool EditOptions_DropMediaCaptions { get; set; }
            public bool EditOptions_NoForwards { get; set; }

            // Flattened properties from MessageFilterOptions
            public string? FilterOptions_AllowedMessageTypes { get; set; } // JSON string
            public string? FilterOptions_AllowedMimeTypes { get; set; } // JSON string
            public string? FilterOptions_ContainsText { get; set; }
            public bool FilterOptions_ContainsTextIsRegex { get; set; }
            public int FilterOptions_ContainsTextRegexOptions { get; set; } // int value
            public string? FilterOptions_AllowedSenderUserIds { get; set; } // JSON string
            public string? FilterOptions_BlockedSenderUserIds { get; set; } // JSON string
            public bool FilterOptions_IgnoreEditedMessages { get; set; }
            public bool FilterOptions_IgnoreServiceMessages { get; set; }
            public int? FilterOptions_MinMessageLength { get; set; }
            public int? FilterOptions_MaxMessageLength { get; set; }

            // Converts the DTO to the domain entity
            public ForwardingRule ToDomainEntity(IReadOnlyList<TextReplacement>? textReplacements = null)
            {
                var editOptions = new MessageEditOptions(
                    EditOptions_PrependText,
                    EditOptions_AppendText,
                    textReplacements, // Pass the separately fetched TextReplacements
                    EditOptions_RemoveSourceForwardHeader,
                    EditOptions_RemoveLinks,
                    EditOptions_StripFormatting,
                    EditOptions_CustomFooter,
                    EditOptions_DropAuthor,
                    EditOptions_DropMediaCaptions,
                    EditOptions_NoForwards
                );

                var filterOptions = new MessageFilterOptions(
                    JsonSerializer.Deserialize<List<string>>(FilterOptions_AllowedMessageTypes ?? "[]", _jsonOptions),
                    JsonSerializer.Deserialize<List<string>>(FilterOptions_AllowedMimeTypes ?? "[]", _jsonOptions),
                    FilterOptions_ContainsText,
                    FilterOptions_ContainsTextIsRegex,
                    (RegexOptions)FilterOptions_ContainsTextRegexOptions,
                    JsonSerializer.Deserialize<List<long>>(FilterOptions_AllowedSenderUserIds ?? "[]", _jsonOptions),
                    JsonSerializer.Deserialize<List<long>>(FilterOptions_BlockedSenderUserIds ?? "[]", _jsonOptions),
                    FilterOptions_IgnoreEditedMessages,
                    FilterOptions_IgnoreServiceMessages,
                    FilterOptions_MinMessageLength,
                    FilterOptions_MaxMessageLength
                );

                return new ForwardingRule(
                    RuleName,
                    IsEnabled,
                    SourceChannelId,
                    JsonSerializer.Deserialize<List<long>>(TargetChannelIds ?? "[]", _jsonOptions) ?? [],
                    editOptions,
                    filterOptions
                );
            }
        }

        // DTO for TextReplacement
        private class TextReplacementDbDto
        {
            public int Id { get; set; }
            public string EditOptionsForwardingRuleName { get; set; } = default!; // Foreign Key
            public string Find { get; set; } = default!;
            public string ReplaceWith { get; set; } = default!;
            public bool IsRegex { get; set; }
            public int RegexOptions { get; set; } // int value

            public TextReplacement ToDomainEntity()
            {
                return new TextReplacement(
                    Find,
                    ReplaceWith,
                    IsRegex,
                    (RegexOptions)RegexOptions
                );
            }
        }

        #endregion

        #region Read Operations

        /// <summary>
        /// Retrieves a forwarding rule asynchronously based on its name.
        /// This operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="ruleName">The name of the forwarding rule.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        /// <returns>The found forwarding rule or null if not found.</returns>
        public async Task<ForwardingRule?> GetByIdAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: GetByIdAsync called with null or empty ruleName.");
                return null;
            }
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rule by RuleName: {RuleName}", ruleName);

            return await _retryPolicy.ExecuteAsync(async () => // Apply retry policy
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                // Fetch the main rule and its text replacements in a single query using QueryMultiple
                var sql = @"
                    SELECT
                        RuleName, IsEnabled, SourceChannelId, TargetChannelIds,
                        EditOptions_PrependText, EditOptions_AppendText, EditOptions_RemoveSourceForwardHeader, EditOptions_RemoveLinks,
                        EditOptions_StripFormatting, EditOptions_CustomFooter, EditOptions_DropAuthor, EditOptions_DropMediaCaptions, EditOptions_NoForwards,
                        FilterOptions_AllowedMessageTypes, FilterOptions_AllowedMimeTypes, FilterOptions_ContainsText, FilterOptions_ContainsTextIsRegex,
                        FilterOptions_ContainsTextRegexOptions, FilterOptions_AllowedSenderUserIds, FilterOptions_BlockedSenderUserIds,
                        FilterOptions_IgnoreEditedMessages, FilterOptions_IgnoreServiceMessages, FilterOptions_MinMessageLength, FilterOptions_MaxMessageLength
                    FROM ForwardingRules
                    WHERE RuleName = @RuleName;

                    SELECT
                        Id, EditOptionsForwardingRuleName, Find, ReplaceWith, IsRegex, RegexOptions
                    FROM TextReplacement -- <--- CORRECTED TABLE NAME HERE
                    WHERE EditOptionsForwardingRuleName = @RuleName;";

                using var multi = await connection.QueryMultipleAsync(sql, new { RuleName = ruleName });

                var ruleDto = await multi.ReadFirstOrDefaultAsync<ForwardingRuleDbDto>();
                if (ruleDto == null)
                {
                    return null;
                }

                var textReplacementDtos = (await multi.ReadAsync<TextReplacementDbDto>()).ToList();
                var textReplacements = textReplacementDtos.Select(dto => dto.ToDomainEntity()).ToList();

                return ruleDto.ToDomainEntity(textReplacements);
            });
        }

        /// <summary>
        /// Retrieves all forwarding rules asynchronously.
        /// This operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        /// <returns>A list of all forwarding rules.</returns>
        public async Task<IEnumerable<ForwardingRule>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching all forwarding rules.");

            return await _retryPolicy.ExecuteAsync(async () => // Apply retry policy
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                var sql = @"
                    SELECT
                        RuleName, IsEnabled, SourceChannelId, TargetChannelIds,
                        EditOptions_PrependText, EditOptions_AppendText, EditOptions_RemoveSourceForwardHeader, EditOptions_RemoveLinks,
                        EditOptions_StripFormatting, EditOptions_CustomFooter, EditOptions_DropAuthor, EditOptions_DropMediaCaptions, EditOptions_NoForwards,
                        FilterOptions_AllowedMessageTypes, FilterOptions_AllowedMimeTypes, FilterOptions_ContainsText, FilterOptions_ContainsTextIsRegex,
                        FilterOptions_ContainsTextRegexOptions, FilterOptions_AllowedSenderUserIds, FilterOptions_BlockedSenderUserIds,
                        FilterOptions_IgnoreEditedMessages, FilterOptions_IgnoreServiceMessages, FilterOptions_MinMessageLength, FilterOptions_MaxMessageLength
                    FROM ForwardingRules
                    ORDER BY RuleName;

                    SELECT
                        Id, EditOptionsForwardingRuleName, Find, ReplaceWith, IsRegex, RegexOptions
                    FROM TextReplacement;";

                using var multi = await connection.QueryMultipleAsync(sql);

                var ruleDtos = (await multi.ReadAsync<ForwardingRuleDbDto>()).ToList();
                var allTextReplacementDtos = (await multi.ReadAsync<TextReplacementDbDto>()).ToList();

                var rules = new List<ForwardingRule>();
                foreach (var ruleDto in ruleDtos)
                {
                    var ruleTextReplacements = allTextReplacementDtos
                        .Where(tr => tr.EditOptionsForwardingRuleName == ruleDto.RuleName)
                        .Select(dto => dto.ToDomainEntity())
                        .ToList();
                    rules.Add(ruleDto.ToDomainEntity(ruleTextReplacements));
                }
                return rules;
            });
        }

        /// <summary>
        /// Retrieves forwarding rules asynchronously based on the source channel ID.
        /// This operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="sourceChannelId">The source channel ID.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        /// <returns>A list of forwarding rules associated with the source channel.</returns>
        public async Task<IEnumerable<ForwardingRule>> GetBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rules by SourceChannelId: {SourceChannelId}.", sourceChannelId);

            return await _retryPolicy.ExecuteAsync(async () => // Apply retry policy
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                var sql = @"
                    SELECT
                        RuleName, IsEnabled, SourceChannelId, TargetChannelIds,
                        EditOptions_PrependText, EditOptions_AppendText, EditOptions_RemoveSourceForwardHeader, EditOptions_RemoveLinks,
                        EditOptions_StripFormatting, EditOptions_CustomFooter, EditOptions_DropAuthor, EditOptions_DropMediaCaptions, EditOptions_NoForwards,
                        FilterOptions_AllowedMessageTypes, FilterOptions_AllowedMimeTypes, FilterOptions_ContainsText, FilterOptions_ContainsTextIsRegex,
                        FilterOptions_ContainsTextRegexOptions, FilterOptions_AllowedSenderUserIds, FilterOptions_BlockedSenderUserIds,
                        FilterOptions_IgnoreEditedMessages, FilterOptions_IgnoreServiceMessages, FilterOptions_MinMessageLength, FilterOptions_MaxMessageLength
                    FROM ForwardingRules
                    WHERE SourceChannelId = @SourceChannelId
                    ORDER BY RuleName;

                    SELECT
                        Id, EditOptionsForwardingRuleName, Find, ReplaceWith, IsRegex, RegexOptions
                    FROM TextReplacement -- <--- CORRECTED TABLE NAME HERE
                    WHERE EditOptionsForwardingRuleName IN (SELECT RuleName FROM ForwardingRules WHERE SourceChannelId = @SourceChannelId);"; // Only fetch relevant replacements

                using var multi = await connection.QueryMultipleAsync(sql, new { SourceChannelId = sourceChannelId });

                var ruleDtos = (await multi.ReadAsync<ForwardingRuleDbDto>()).ToList();
                var allTextReplacementDtos = (await multi.ReadAsync<TextReplacementDbDto>()).ToList();

                var rules = new List<ForwardingRule>();
                foreach (var ruleDto in ruleDtos)
                {
                    var ruleTextReplacements = allTextReplacementDtos
                        .Where(tr => tr.EditOptionsForwardingRuleName == ruleDto.RuleName)
                        .Select(dto => dto.ToDomainEntity())
                        .ToList();
                    rules.Add(ruleDto.ToDomainEntity(ruleTextReplacements));
                }
                return rules;
            });
        }

        /// <summary>
        /// Retrieves forwarding rules asynchronously in a paginated manner.
        /// This operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="pageNumber">The page number (starts from 1).</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        /// <returns>A list of forwarding rules for the specified page.</returns>
        public async Task<IEnumerable<ForwardingRule>> GetPaginatedAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            if (pageNumber <= 0)
            {
                pageNumber = 1;
            }

            if (pageSize <= 0)
            {
                pageSize = 10; // Default page size
            }

            _logger.LogTrace("ForwardingRuleRepository: Fetching paginated forwarding rules - Page: {PageNumber}, Size: {PageSize}.", pageNumber, pageSize);

            return await _retryPolicy.ExecuteAsync(async () => // Apply retry policy
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                var offset = (pageNumber - 1) * pageSize;

                // First, fetch RuleNames for the current page to limit the subsequent join
                var ruleNamesInPageSql = @"
                    SELECT RuleName
                    FROM ForwardingRules
                    ORDER BY RuleName
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

                var pagedRuleNames = (await connection.QueryAsync<string>(ruleNamesInPageSql, new { Offset = offset, PageSize = pageSize })).ToList();

                if (!pagedRuleNames.Any())
                {
                    return Enumerable.Empty<ForwardingRule>();
                }

                // Now fetch the full rules and their text replacements for the identified rule names
                var sql = @"
                    SELECT
                        RuleName, IsEnabled, SourceChannelId, TargetChannelIds,
                        EditOptions_PrependText, EditOptions_AppendText, EditOptions_RemoveSourceForwardHeader, EditOptions_RemoveLinks,
                        EditOptions_StripFormatting, EditOptions_CustomFooter, EditOptions_DropAuthor, EditOptions_DropMediaCaptions, EditOptions_NoForwards,
                        FilterOptions_AllowedMessageTypes, FilterOptions_AllowedMimeTypes, FilterOptions_ContainsText, FilterOptions_ContainsTextIsRegex,
                        FilterOptions_ContainsTextRegexOptions, FilterOptions_AllowedSenderUserIds, FilterOptions_BlockedSenderUserIds,
                        FilterOptions_IgnoreEditedMessages, FilterOptions_IgnoreServiceMessages, FilterOptions_MinMessageLength, FilterOptions_MaxMessageLength
                    FROM ForwardingRules
                    WHERE RuleName IN @RuleNames
                    ORDER BY RuleName;

                    SELECT
                        Id, EditOptionsForwardingRuleName, Find, ReplaceWith, IsRegex, RegexOptions
                    FROM TextReplacement -- <--- CORRECTED TABLE NAME HERE
                    WHERE EditOptionsForwardingRuleName IN @RuleNames;"; // Fetch replacements for the paged rules

                using var multi = await connection.QueryMultipleAsync(sql, new { RuleNames = pagedRuleNames });

                var ruleDtos = (await multi.ReadAsync<ForwardingRuleDbDto>()).ToList();
                var allTextReplacementDtos = (await multi.ReadAsync<TextReplacementDbDto>()).ToList();

                var rules = new List<ForwardingRule>();
                foreach (var ruleDto in ruleDtos)
                {
                    var ruleTextReplacements = allTextReplacementDtos
                        .Where(tr => tr.EditOptionsForwardingRuleName == ruleDto.RuleName)
                        .Select(dto => dto.ToDomainEntity())
                        .ToList();
                    rules.Add(ruleDto.ToDomainEntity(ruleTextReplacements));
                }
                return rules;
            });
        }

        /// <summary>
        /// Retrieves the total count of forwarding rules asynchronously.
        /// This operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        /// <returns>The total count of forwarding rules.</returns>
        public async Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Getting total count of forwarding rules.");

            return await _retryPolicy.ExecuteAsync(async () => // Apply retry policy
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                var sql = "SELECT COUNT(*) FROM ForwardingRules;";
                return await connection.ExecuteScalarAsync<int>(sql);
            });
        }

        #endregion

        #region Write Operations (with SaveChangesAsync inside Repository)

        /// <summary>
        /// Adds a new forwarding rule asynchronously.
        /// The save operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="rule">The forwarding rule to add.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        public async Task AddAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a ForwardingRule with null or empty RuleName.");
                throw new ArgumentException("RuleName cannot be null or empty.", nameof(rule.RuleName));
            }
            _logger.LogInformation("ForwardingRuleRepository: Adding forwarding rule with RuleName: {RuleName}, SourceChannelId: {SourceChannelId}",
                                   rule.RuleName, rule.SourceChannelId);
            try
            {
                await _retryPolicy.ExecuteAsync(async () => // Apply retry policy for write operations
                {
                    using var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync(cancellationToken);

                    // Start a transaction for atomicity
                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        var insertRuleSql = @"
                            INSERT INTO ForwardingRules (
                                RuleName, IsEnabled, SourceChannelId, TargetChannelIds,
                                EditOptions_PrependText, EditOptions_AppendText, EditOptions_RemoveSourceForwardHeader,
                                EditOptions_RemoveLinks, EditOptions_StripFormatting, EditOptions_CustomFooter,
                                EditOptions_DropAuthor, EditOptions_DropMediaCaptions, EditOptions_NoForwards,
                                FilterOptions_AllowedMessageTypes, FilterOptions_AllowedMimeTypes, FilterOptions_ContainsText,
                                FilterOptions_ContainsTextIsRegex, FilterOptions_ContainsTextRegexOptions, FilterOptions_AllowedSenderUserIds,
                                FilterOptions_BlockedSenderUserIds, FilterOptions_IgnoreEditedMessages, FilterOptions_IgnoreServiceMessages,
                                FilterOptions_MinMessageLength, FilterOptions_MaxMessageLength
                            ) VALUES (
                                @RuleName, @IsEnabled, @SourceChannelId, @TargetChannelIds,
                                @EditOptions_PrependText, @EditOptions_AppendText, @EditOptions_RemoveSourceForwardHeader,
                                @EditOptions_RemoveLinks, @EditOptions_StripFormatting, @EditOptions_CustomFooter,
                                @EditOptions_DropAuthor, @EditOptions_DropMediaCaptions, @EditOptions_NoForwards,
                                @FilterOptions_AllowedMessageTypes, @FilterOptions_AllowedMimeTypes, @FilterOptions_ContainsText,
                                @FilterOptions_ContainsTextIsRegex, @FilterOptions_ContainsTextRegexOptions, @FilterOptions_AllowedSenderUserIds,
                                @FilterOptions_BlockedSenderUserIds, @FilterOptions_IgnoreEditedMessages, @FilterOptions_IgnoreServiceMessages,
                                @FilterOptions_MinMessageLength, @FilterOptions_MaxMessageLength
                            );";

                        // Prepare parameters from the domain entity
                        var ruleParams = new
                        {
                            rule.RuleName,
                            rule.IsEnabled,
                            rule.SourceChannelId,
                            TargetChannelIds = JsonSerializer.Serialize(rule.TargetChannelIds, _jsonOptions),
                            EditOptions_PrependText = rule.EditOptions.PrependText,
                            EditOptions_AppendText = rule.EditOptions.AppendText,
                            EditOptions_RemoveSourceForwardHeader = rule.EditOptions.RemoveSourceForwardHeader,
                            EditOptions_RemoveLinks = rule.EditOptions.RemoveLinks,
                            EditOptions_StripFormatting = rule.EditOptions.StripFormatting,
                            EditOptions_CustomFooter = rule.EditOptions.CustomFooter,
                            EditOptions_DropAuthor = rule.EditOptions.DropAuthor,
                            EditOptions_DropMediaCaptions = rule.EditOptions.DropMediaCaptions,
                            EditOptions_NoForwards = rule.EditOptions.NoForwards,
                            FilterOptions_AllowedMessageTypes = JsonSerializer.Serialize(rule.FilterOptions.AllowedMessageTypes, _jsonOptions),
                            FilterOptions_AllowedMimeTypes = JsonSerializer.Serialize(rule.FilterOptions.AllowedMimeTypes, _jsonOptions),
                            FilterOptions_ContainsText = rule.FilterOptions.ContainsText,
                            FilterOptions_ContainsTextIsRegex = rule.FilterOptions.ContainsTextIsRegex,
                            FilterOptions_ContainsTextRegexOptions = (int)rule.FilterOptions.ContainsTextRegexOptions,
                            FilterOptions_AllowedSenderUserIds = JsonSerializer.Serialize(rule.FilterOptions.AllowedSenderUserIds, _jsonOptions),
                            FilterOptions_BlockedSenderUserIds = JsonSerializer.Serialize(rule.FilterOptions.BlockedSenderUserIds, _jsonOptions),
                            FilterOptions_IgnoreEditedMessages = rule.FilterOptions.IgnoreEditedMessages,
                            FilterOptions_IgnoreServiceMessages = rule.FilterOptions.IgnoreServiceMessages,
                            FilterOptions_MinMessageLength = rule.FilterOptions.MinMessageLength,
                            FilterOptions_MaxMessageLength = rule.FilterOptions.MaxMessageLength
                        };

                        _ = await connection.ExecuteAsync(insertRuleSql, ruleParams, transaction: transaction);

                        // Insert TextReplacements if any
                        if (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any())
                        {
                            var insertReplacementSql = @"
                                INSERT INTO TextReplacement ( -- <--- CORRECTED TABLE NAME HERE
                                    EditOptionsForwardingRuleName, Find, ReplaceWith, IsRegex, RegexOptions
                                ) VALUES (
                                    @EditOptionsForwardingRuleName, @Find, @ReplaceWith, @IsRegex, @RegexOptions
                                );";

                            foreach (var replacement in rule.EditOptions.TextReplacements)
                            {
                                var replacementParams = new
                                {
                                    EditOptionsForwardingRuleName = rule.RuleName,
                                    replacement.Find,
                                    replacement.ReplaceWith,
                                    replacement.IsRegex,
                                    RegexOptions = (int)replacement.RegexOptions
                                };
                                _ = await connection.ExecuteAsync(insertReplacementSql, replacementParams, transaction: transaction);
                            }
                        }
                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Wrap the exception in DbUpdateException for consistency with EF Core's error handling
                        throw new DbUpdateException("Failed to add forwarding rule with Dapper transaction.", ex);
                    }
                });
                _logger.LogInformation("ForwardingRuleRepository: Successfully added forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error adding forwarding rule {RuleName} to the database after retries.", rule.RuleName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForwardingRuleRepository: An unexpected error occurred while adding forwarding rule {RuleName}.", rule.RuleName);
                throw;
            }
        }

        /// <summary>
        /// Updates an existing forwarding rule asynchronously.
        /// The save operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="rule">The forwarding rule to update.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        public async Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update with a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update a ForwardingRule without a valid RuleName (used as identifier).");
                throw new ArgumentException("RuleName cannot be null or empty for update identification.", nameof(rule.RuleName));
            }
            _logger.LogInformation("ForwardingRuleRepository: Updating forwarding rule with RuleName: {RuleName}", rule.RuleName);
            try
            {
                await _retryPolicy.ExecuteAsync(async () => // Apply retry policy for write operations
                {
                    using var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync(cancellationToken);

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        var updateRuleSql = @"
                            UPDATE ForwardingRules SET
                                IsEnabled = @IsEnabled,
                                SourceChannelId = @SourceChannelId,
                                TargetChannelIds = @TargetChannelIds,
                                EditOptions_PrependText = @EditOptions_PrependText,
                                EditOptions_AppendText = @EditOptions_AppendText,
                                EditOptions_RemoveSourceForwardHeader = @EditOptions_RemoveSourceForwardHeader,
                                EditOptions_RemoveLinks = @EditOptions_RemoveLinks,
                                EditOptions_StripFormatting = @EditOptions_StripFormatting,
                                EditOptions_CustomFooter = @EditOptions_CustomFooter,
                                EditOptions_DropAuthor = @EditOptions_DropAuthor,
                                EditOptions_DropMediaCaptions = @EditOptions_DropMediaCaptions,
                                EditOptions_NoForwards = @EditOptions_NoForwards,
                                FilterOptions_AllowedMessageTypes = @FilterOptions_AllowedMessageTypes,
                                FilterOptions_AllowedMimeTypes = @FilterOptions_AllowedMimeTypes,
                                FilterOptions_ContainsText = @FilterOptions_ContainsText,
                                FilterOptions_ContainsTextIsRegex = @FilterOptions_ContainsTextIsRegex,
                                FilterOptions_ContainsTextRegexOptions = @FilterOptions_ContainsTextRegexOptions,
                                FilterOptions_AllowedSenderUserIds = @FilterOptions_AllowedSenderUserIds,
                                FilterOptions_BlockedSenderUserIds = @FilterOptions_BlockedSenderUserIds,
                                FilterOptions_IgnoreEditedMessages = @FilterOptions_IgnoreEditedMessages,
                                FilterOptions_IgnoreServiceMessages = @FilterOptions_IgnoreServiceMessages,
                                FilterOptions_MinMessageLength = @FilterOptions_MinMessageLength,
                                FilterOptions_MaxMessageLength = @FilterOptions_MaxMessageLength
                            WHERE RuleName = @RuleName;";

                        // Prepare parameters from the domain entity
                        var ruleParams = new
                        {
                            rule.RuleName,
                            rule.IsEnabled,
                            rule.SourceChannelId,
                            TargetChannelIds = JsonSerializer.Serialize(rule.TargetChannelIds, _jsonOptions),
                            EditOptions_PrependText = rule.EditOptions.PrependText,
                            EditOptions_AppendText = rule.EditOptions.AppendText,
                            EditOptions_RemoveSourceForwardHeader = rule.EditOptions.RemoveSourceForwardHeader,
                            EditOptions_RemoveLinks = rule.EditOptions.RemoveLinks,
                            EditOptions_StripFormatting = rule.EditOptions.StripFormatting,
                            EditOptions_CustomFooter = rule.EditOptions.CustomFooter,
                            EditOptions_DropAuthor = rule.EditOptions.DropAuthor,
                            EditOptions_DropMediaCaptions = rule.EditOptions.DropMediaCaptions,
                            EditOptions_NoForwards = rule.EditOptions.NoForwards,
                            FilterOptions_AllowedMessageTypes = JsonSerializer.Serialize(rule.FilterOptions.AllowedMessageTypes, _jsonOptions),
                            FilterOptions_AllowedMimeTypes = JsonSerializer.Serialize(rule.FilterOptions.AllowedMimeTypes, _jsonOptions),
                            FilterOptions_ContainsText = rule.FilterOptions.ContainsText,
                            FilterOptions_ContainsTextIsRegex = rule.FilterOptions.ContainsTextIsRegex,
                            FilterOptions_ContainsTextRegexOptions = (int)rule.FilterOptions.ContainsTextRegexOptions,
                            FilterOptions_AllowedSenderUserIds = JsonSerializer.Serialize(rule.FilterOptions.AllowedSenderUserIds, _jsonOptions),
                            FilterOptions_BlockedSenderUserIds = JsonSerializer.Serialize(rule.FilterOptions.BlockedSenderUserIds, _jsonOptions),
                            FilterOptions_IgnoreEditedMessages = rule.FilterOptions.IgnoreEditedMessages,
                            FilterOptions_IgnoreServiceMessages = rule.FilterOptions.IgnoreServiceMessages,
                            FilterOptions_MinMessageLength = rule.FilterOptions.MinMessageLength,
                            FilterOptions_MaxMessageLength = rule.FilterOptions.MaxMessageLength
                        };

                        var rowsAffected = await connection.ExecuteAsync(updateRuleSql, ruleParams, transaction: transaction);

                        if (rowsAffected == 0)
                        {
                            // If no rows were affected, the rule was not found or a concurrency conflict occurred.
                            // Re-throw as DbUpdateConcurrencyException for consistent error handling.
                            throw new DbUpdateConcurrencyException($"Forwarding rule with RuleName '{rule.RuleName}' not found or modified by another process.");
                        }

                        // Delete existing TextReplacements for this rule
                        var deleteReplacementsSql = "DELETE FROM TextReplacement WHERE EditOptionsForwardingRuleName = @RuleName;"; // <--- CORRECTED TABLE NAME HERE
                        _ = await connection.ExecuteAsync(deleteReplacementsSql, new { rule.RuleName }, transaction: transaction);

                        // Insert new TextReplacements
                        if (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any())
                        {
                            var insertReplacementSql = @"
                                INSERT INTO TextReplacement ( -- <--- CORRECTED TABLE NAME HERE
                                    EditOptionsForwardingRuleName, Find, ReplaceWith, IsRegex, RegexOptions
                                ) VALUES (
                                    @EditOptionsForwardingRuleName, @Find, @ReplaceWith, @IsRegex, @RegexOptions
                                );";

                            foreach (var replacement in rule.EditOptions.TextReplacements)
                            {
                                var replacementParams = new
                                {
                                    EditOptionsForwardingRuleName = rule.RuleName,
                                    replacement.Find,
                                    replacement.ReplaceWith,
                                    replacement.IsRegex,
                                    RegexOptions = (int)replacement.RegexOptions
                                };
                                _ = await connection.ExecuteAsync(insertReplacementSql, replacementParams, transaction: transaction);
                            }
                        }
                        transaction.Commit();
                    }
                    catch (DbUpdateConcurrencyException) // Re-throw concurrency exception
                    {
                        transaction.Rollback();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Wrap the exception in DbUpdateException for consistency with EF Core's error handling
                        throw new DbUpdateException($"Failed to update forwarding rule '{rule.RuleName}' with Dapper transaction.", ex);
                    }
                });
                _logger.LogInformation("ForwardingRuleRepository: Successfully updated forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateConcurrencyException concEx)
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while updating forwarding rule {RuleName}. The rule may have been modified or deleted by another user.", rule.RuleName);
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error updating forwarding rule {RuleName} in the database after retries.", rule.RuleName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForwardingRuleRepository: An unexpected error occurred while updating forwarding rule {RuleName}.", rule.RuleName);
                throw;
            }
        }

        /// <summary>
        /// Deletes a forwarding rule asynchronously based on its name.
        /// The delete and save operations are protected by the Polly retry policy.
        /// </summary>
        /// <param name="ruleName">The name of the forwarding rule to delete.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        public async Task DeleteAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: DeleteAsync called with null or empty ruleName.");
                return;
            }
            _logger.LogInformation("ForwardingRuleRepository: Attempting to delete forwarding rule with RuleName: {RuleName}", ruleName);

            // First, check if the rule exists to provide more specific logging and potential concurrency handling
            // This initial check also benefits from Polly's retry.
            var ruleExists = await _retryPolicy.ExecuteAsync(async () =>
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                var sql = "SELECT COUNT(*) FROM ForwardingRules WHERE RuleName = @RuleName;";
                return await connection.ExecuteScalarAsync<int>(sql, new { RuleName = ruleName }) > 0;
            });

            if (!ruleExists)
            {
                _logger.LogWarning("ForwardingRuleRepository: Forwarding rule with RuleName {RuleName} not found for deletion.", ruleName);
                return;
            }

            try
            {
                await _retryPolicy.ExecuteAsync(async () => // Apply retry policy for delete and save operations
                {
                    using var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync(cancellationToken);

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        // Delete TextReplacements first due to foreign key constraint
                        var deleteReplacementsSql = "DELETE FROM TextReplacement WHERE EditOptionsForwardingRuleName = @RuleName;"; // <--- CORRECTED TABLE NAME HERE
                        _ = await connection.ExecuteAsync(deleteReplacementsSql, new { RuleName = ruleName }, transaction: transaction);

                        // Then delete the main rule
                        var deleteRuleSql = "DELETE FROM ForwardingRules WHERE RuleName = @RuleName;";
                        var rowsAffected = await connection.ExecuteAsync(deleteRuleSql, new { RuleName = ruleName }, transaction: transaction);

                        if (rowsAffected == 0)
                        {
                            // This might indicate a concurrency issue where the rule was deleted by another process
                            // between the initial check and this actual delete.
                            throw new DbUpdateConcurrencyException($"Forwarding rule with RuleName '{ruleName}' was not found for deletion or was deleted by another process.");
                        }

                        transaction.Commit();
                    }
                    catch (DbUpdateConcurrencyException) // Re-throw concurrency exception
                    {
                        transaction.Rollback();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Wrap the exception in DbUpdateException for consistency with EF Core's error handling
                        throw new DbUpdateException($"Failed to delete forwarding rule '{ruleName}' with Dapper transaction.", ex);
                    }
                });
                _logger.LogInformation("ForwardingRuleRepository: Successfully deleted forwarding rule: {RuleName}", ruleName);
            }
            catch (DbUpdateConcurrencyException concEx)
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while deleting forwarding rule {RuleName}.", ruleName);
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error deleting forwarding rule {RuleName} from the database after retries. It might be in use.", ruleName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForwardingRuleRepository: An unexpected error occurred while deleting forwarding rule {RuleName}.", ruleName);
                throw;
            }
        }
    }
    #endregion
}