// // File: Infrastructure/Features/Forwarding/Repositories/ForwardingRuleRepository.cs

#region Usings
// Standard .NET & NuGet
using Dapper; // Added for Dapper
// Project specific
using Domain.Features.Forwarding.Entities;     // For ForwardingRule entity
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository interface
using Domain.Features.Forwarding.ValueObjects; // For MessageEditOptions, MessageFilterOptions, TextReplacement
using Infrastructure.Data;                     // For DbProviderService / DatabaseProvider
using Microsoft.EntityFrameworkCore; // Still needed for DbUpdateConcurrencyException type check in Polly
using Microsoft.Extensions.Configuration; // Added to get connection string
using Microsoft.Extensions.Logging;   // For logging
using Npgsql;
using Polly; // For Polly policies
using Polly.Retry; // For Retry policy
using System.Data; // For IDbConnection
using System.Data.Common; // For DbConnection / DbException
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
    /// <remarks>
    /// Provider-agnostic: the original version hard-coded <c>NpgsqlConnection</c>,
    /// schema-qualified <c>public."Table"</c> identifiers and <c>::jsonb</c> casts, which made
    /// every forwarding API a guaranteed 500 on SQLite and SQL Server. SQL is now built per
    /// provider from <see cref="DbProviderService"/>, mirroring <see cref="SettingsDbHelper"/>.
    /// </remarks>
    public class ForwardingRuleRepository : IForwardingRuleRepository
    {
        private readonly string _connectionString;
        private readonly DatabaseProvider _provider;
        private readonly ILogger<ForwardingRuleRepository> _logger;
        private readonly AsyncRetryPolicy _retryPolicy;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

        /// <summary>
        /// Initializes a new instance of the ForwardingRuleRepository class.
        /// </summary>
        /// <param name="configuration">The application's configuration, used to retrieve the database connection string.</param>
        /// <param name="providerService">The resolved database provider (PostgreSQL / SQLite / SQL Server).</param>
        /// <param name="logger">The logger for logging information and errors.</param>
        public ForwardingRuleRepository(IConfiguration configuration, DbProviderService providerService, ILogger<ForwardingRuleRepository> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("DefaultConnection string not found in configuration.");
            _provider = providerService.Provider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Transient-failure retry that ignores unique-constraint violations, which are
            // business errors rather than transient ones (Postgres '23505', SQLite constraint).
            _retryPolicy = Policy
               .Handle<DbException>(ex => !IsUniqueViolation(ex))
               .WaitAndRetryAsync(
                   retryCount: 3,
                   sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                   onRetry: (exception, timeSpan, retryAttempt, context) =>
                   {
                       _logger.LogWarning(exception,
                           "ForwardingRuleRepository: Transient database error. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                           timeSpan, retryAttempt, exception.Message);
                   });
        }

        private static bool IsUniqueViolation(DbException ex) => ex switch
        {
            PostgresException pg => pg.SqlState == "23505",
            Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT
            _ => false
        };

        #region Provider-specific SQL helpers

        /// <summary>Quotes one identifier: PostgreSQL/SQLite use double quotes, SQL Server brackets.</summary>
        private string Q(string identifier) => _provider switch
        {
            DatabaseProvider.SqlServer => $"[{identifier}]",
            _ => $"\"{identifier}\""
        };

        /// <summary>Table reference: only PostgreSQL has the public schema; SQLite/SQL Server use plain names.</summary>
        private string T(string table) => _provider == DatabaseProvider.Postgres
            ? $"public.{Q(table)}"
            : Q(table);

        /// <summary>JSON parameter: only PostgreSQL needs the explicit ::jsonb cast.</summary>
        private string J(string parameter) => _provider == DatabaseProvider.Postgres
            ? $"{parameter}::jsonb"
            : parameter;

        /// <summary>Two-statement pager: OFFSET/FETCH on SQL Server, LIMIT/OFFSET elsewhere.</summary>
        private string PagingSql => _provider switch
        {
            DatabaseProvider.SqlServer => $"""
                SELECT * FROM {T("ForwardingRules")} WHERE {Q("RuleName")} IN (
                    SELECT {Q("RuleName")} FROM {T("ForwardingRules")} ORDER BY {Q("RuleName")} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
                );
                SELECT * FROM {T("ForwardingRuleTextReplacements")} WHERE {Q("ForwardingRuleName")} IN (
                    SELECT {Q("RuleName")} FROM {T("ForwardingRules")} ORDER BY {Q("RuleName")} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
                );
                """,
            _ => $"""
                SELECT * FROM {T("ForwardingRules")} WHERE {Q("RuleName")} IN (
                    SELECT {Q("RuleName")} FROM {T("ForwardingRules")} ORDER BY {Q("RuleName")} LIMIT @PageSize OFFSET @Offset
                );
                SELECT * FROM {T("ForwardingRuleTextReplacements")} WHERE {Q("ForwardingRuleName")} IN (
                    SELECT {Q("RuleName")} FROM {T("ForwardingRules")} ORDER BY {Q("RuleName")} LIMIT @PageSize OFFSET @Offset
                );
                """
        };

        /// <summary>Opens a provider-correct connection (caller owns dispose/open).</summary>
        private DbConnection CreateConnection() => _provider switch
        {
            DatabaseProvider.Postgres => new NpgsqlConnection(_connectionString),
            DatabaseProvider.SQLite => new Microsoft.Data.Sqlite.SqliteConnection(_connectionString),
            DatabaseProvider.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(_connectionString),
            _ => throw new NotSupportedException($"Database provider '{_provider}' is not supported for forwarding rules.")
        };

        #endregion

        #region Internal DTOs for Dapper Mapping

        private class ForwardingRuleWithReplacementsDto
        {
            public string RuleName { get; set; } = default!;
            public bool IsEnabled { get; set; }
            public long SourceChannelId { get; set; }
            public string TargetChannelIds { get; set; } = default!; // JSON from DB
            public string? EditOptions_PrependText { get; set; }
            public string? EditOptions_AppendText { get; set; }
            public bool EditOptions_RemoveSourceForwardHeader { get; set; }
            public bool EditOptions_RemoveLinks { get; set; }
            public bool EditOptions_StripFormatting { get; set; }
            public string? EditOptions_CustomFooter { get; set; }
            public bool EditOptions_DropAuthor { get; set; }
            public bool EditOptions_DropMediaCaptions { get; set; }
            public bool EditOptions_NoForwards { get; set; }
            public string FilterOptions_AllowedMessageTypes { get; set; } = default!; // JSON from DB
            public string FilterOptions_AllowedMimeTypes { get; set; } = default!; // JSON from DB
            public string? FilterOptions_ContainsText { get; set; }
            public bool FilterOptions_ContainsTextIsRegex { get; set; }
            public int FilterOptions_ContainsTextRegexOptions { get; set; }
            public string FilterOptions_AllowedSenderUserIds { get; set; } = default!; // JSON from DB
            public string FilterOptions_BlockedSenderUserIds { get; set; } = default!; // JSON from DB
            public bool FilterOptions_IgnoreEditedMessages { get; set; }
            public bool FilterOptions_IgnoreServiceMessages { get; set; }
            public int? FilterOptions_MinMessageLength { get; set; }
            public int? FilterOptions_MaxMessageLength { get; set; }
            public string? TextReplacementsJson { get; set; } // JSON array of replacements
        }
        // DTO that mirrors the flattened database structure for ForwardingRule
        private class ForwardingRuleDbDto
        {
            public string RuleName { get; set; } = default!;
            public bool IsEnabled { get; set; }
            public long SourceChannelId { get; set; }
            public string TargetChannelIds { get; set; } = default!;
            public string? EditOptions_PrependText { get; set; }
            public string? EditOptions_AppendText { get; set; }
            public bool EditOptions_RemoveSourceForwardHeader { get; set; }
            public bool EditOptions_RemoveLinks { get; set; }
            public bool EditOptions_StripFormatting { get; set; }
            public string? EditOptions_CustomFooter { get; set; }
            public bool EditOptions_DropAuthor { get; set; }
            public bool EditOptions_DropMediaCaptions { get; set; }
            public bool EditOptions_NoForwards { get; set; }
            public string FilterOptions_AllowedMessageTypes { get; set; } = default!;
            public string FilterOptions_AllowedMimeTypes { get; set; } = default!;
            public string? FilterOptions_ContainsText { get; set; }
            public bool FilterOptions_ContainsTextIsRegex { get; set; }
            public int FilterOptions_ContainsTextRegexOptions { get; set; }
            public string FilterOptions_AllowedSenderUserIds { get; set; } = default!;
            public string FilterOptions_BlockedSenderUserIds { get; set; } = default!;
            public bool FilterOptions_IgnoreEditedMessages { get; set; }
            public bool FilterOptions_IgnoreServiceMessages { get; set; }
            public int? FilterOptions_MinMessageLength { get; set; }
            public int? FilterOptions_MaxMessageLength { get; set; }


            // Converts the DTO to the domain entity
            public ForwardingRule ToDomainEntity(IReadOnlyList<TextReplacement>? textReplacements = null)
            {
                MessageEditOptions editOptions = new(
                    EditOptions_PrependText, EditOptions_AppendText, textReplacements,
                    EditOptions_RemoveSourceForwardHeader, EditOptions_RemoveLinks, EditOptions_StripFormatting,
                    EditOptions_CustomFooter, EditOptions_DropAuthor, EditOptions_DropMediaCaptions,
                    EditOptions_NoForwards
                );
                MessageFilterOptions filterOptions = new(
           JsonSerializer.Deserialize<List<string>>(FilterOptions_AllowedMessageTypes, _jsonOptions) ?? [],
           JsonSerializer.Deserialize<List<string>>(FilterOptions_AllowedMimeTypes, _jsonOptions) ?? [],
           FilterOptions_ContainsText, FilterOptions_ContainsTextIsRegex, (RegexOptions)FilterOptions_ContainsTextRegexOptions,
           JsonSerializer.Deserialize<List<long>>(FilterOptions_AllowedSenderUserIds, _jsonOptions) ?? [],
           JsonSerializer.Deserialize<List<long>>(FilterOptions_BlockedSenderUserIds, _jsonOptions) ?? [],
           FilterOptions_IgnoreEditedMessages, FilterOptions_IgnoreServiceMessages,
           FilterOptions_MinMessageLength, FilterOptions_MaxMessageLength
       );

                return new ForwardingRule(
                    RuleName, IsEnabled, SourceChannelId,
                    JsonSerializer.Deserialize<List<long>>(TargetChannelIds, _jsonOptions) ?? [],
                    editOptions, filterOptions
                );
            }
        }


        // DTO for TextReplacement
        private class TextReplacementDbDto
        {
            public int Id { get; set; }
            public string ForwardingRuleName { get; set; } = default!;
            public string Find { get; set; } = default!;
            public string ReplaceWith { get; set; } = default!;
            public bool IsRegex { get; set; }
            public int RegexOptions { get; set; }

            public TextReplacement ToDomainEntity()
            {
                return new(Find, ReplaceWith, IsRegex, (RegexOptions)RegexOptions);
            }
        }
        private static object CreateRuleParameters(ForwardingRule rule)
        {
            return new
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
                return null;
            }

            string sanitizedRuleName = ruleName.Replace(Environment.NewLine, string.Empty);
            _logger.LogTrace("Fetching forwarding rule by RuleName: {RuleName}", sanitizedRuleName);

            string sql = $@"
                SELECT * FROM {T("ForwardingRules")} WHERE {Q("RuleName")} = @RuleName;
                SELECT * FROM {T("ForwardingRuleTextReplacements")} WHERE {Q("ForwardingRuleName")} = @RuleName;";

            return await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(
                    new CommandDefinition(sql, new { RuleName = ruleName }, cancellationToken: cancellationToken));

                ForwardingRuleDbDto? ruleDto = await multi.ReadFirstOrDefaultAsync<ForwardingRuleDbDto>();
                if (ruleDto == null)
                {
                    return null;
                }

                IEnumerable<TextReplacementDbDto> textReplacementDtos = await multi.ReadAsync<TextReplacementDbDto>();
                List<TextReplacement> textReplacements = textReplacementDtos.Select(dto => dto.ToDomainEntity()).ToList();

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
            _logger.LogTrace("Fetching all forwarding rules.");

            string sql = $@"
                SELECT * FROM {T("ForwardingRules")} ORDER BY {Q("RuleName")};
                SELECT * FROM {T("ForwardingRuleTextReplacements")};";

            return await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(
                    new CommandDefinition(sql, cancellationToken: cancellationToken));

                List<ForwardingRuleDbDto> ruleDtos = (await multi.ReadAsync<ForwardingRuleDbDto>()).ToList();
                ILookup<string, TextReplacementDbDto> replacementsLookup = (await multi.ReadAsync<TextReplacementDbDto>()).ToLookup(r => r.ForwardingRuleName);

                // ILookup's indexer yields an empty sequence for unknown keys; select over it
                // so a rule without replacements can never throw KeyNotFoundException.
                return ruleDtos.Select(dto => dto.ToDomainEntity(
                    replacementsLookup[dto.RuleName].Select(r => r.ToDomainEntity()).ToList())).ToList();
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
            _logger.LogTrace("Fetching forwarding rules by SourceChannelId: {SourceChannelId}.", sourceChannelId);

            string sql = $@"
                SELECT * FROM {T("ForwardingRules")} WHERE {Q("SourceChannelId")} = @SourceChannelId ORDER BY {Q("RuleName")};
                SELECT * FROM {T("ForwardingRuleTextReplacements")} WHERE {Q("ForwardingRuleName")} IN (SELECT {Q("RuleName")} FROM {T("ForwardingRules")} WHERE {Q("SourceChannelId")} = @SourceChannelId);";

            return await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(
                    new CommandDefinition(sql, new { SourceChannelId = sourceChannelId }, cancellationToken: cancellationToken));

                List<ForwardingRuleDbDto> ruleDtos = (await multi.ReadAsync<ForwardingRuleDbDto>()).ToList();
                ILookup<string, TextReplacementDbDto> replacementsLookup = (await multi.ReadAsync<TextReplacementDbDto>()).ToLookup(r => r.ForwardingRuleName);

                return ruleDtos.Select(dto => dto.ToDomainEntity(
                    replacementsLookup[dto.RuleName].Select(r => r.ToDomainEntity()).ToList())).ToList();
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
            if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            int offset = (pageNumber - 1) * pageSize;
            string sql = PagingSql;

            return await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(
                    new CommandDefinition(sql, new { PageSize = pageSize, Offset = offset }, cancellationToken: cancellationToken));

                List<ForwardingRuleDbDto> ruleDtos = (await multi.ReadAsync<ForwardingRuleDbDto>()).ToList();
                ILookup<string, TextReplacementDbDto> replacementsLookup = (await multi.ReadAsync<TextReplacementDbDto>()).ToLookup(r => r.ForwardingRuleName);

                return ruleDtos.Select(dto => dto.ToDomainEntity(
                    replacementsLookup[dto.RuleName].Select(r => r.ToDomainEntity()).ToList())).ToList();
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
            string sql = $@"SELECT COUNT(*) FROM {T("ForwardingRules")};";

            return await _retryPolicy.ExecuteAsync(async (ct) => // Use the token provided by Polly
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(ct);

                // CommandDefinition passes the CancellationToken through to the driver.
                CommandDefinition command = new(sql, cancellationToken: ct);

                return await connection.ExecuteScalarAsync<int>(command);

            }, cancellationToken); // Pass the original token to the Polly policy
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
            ArgumentNullException.ThrowIfNull(rule);

            string insertRuleSql = $@"
                INSERT INTO {T("ForwardingRules")} (
                    {Q("RuleName")}, {Q("IsEnabled")}, {Q("SourceChannelId")}, {Q("TargetChannelIds")}, {Q("EditOptions_PrependText")}, {Q("EditOptions_AppendText")},
                    {Q("EditOptions_RemoveSourceForwardHeader")}, {Q("EditOptions_RemoveLinks")}, {Q("EditOptions_StripFormatting")}, {Q("EditOptions_CustomFooter")},
                    {Q("EditOptions_DropAuthor")}, {Q("EditOptions_DropMediaCaptions")}, {Q("EditOptions_NoForwards")}, {Q("FilterOptions_AllowedMessageTypes")},
                    {Q("FilterOptions_AllowedMimeTypes")}, {Q("FilterOptions_ContainsText")}, {Q("FilterOptions_ContainsTextIsRegex")},
                    {Q("FilterOptions_ContainsTextRegexOptions")}, {Q("FilterOptions_AllowedSenderUserIds")}, {Q("FilterOptions_BlockedSenderUserIds")},
                    {Q("FilterOptions_IgnoreEditedMessages")}, {Q("FilterOptions_IgnoreServiceMessages")}, {Q("FilterOptions_MinMessageLength")}, {Q("FilterOptions_MaxMessageLength")}
                ) VALUES (
                    @RuleName, @IsEnabled, @SourceChannelId, {J("@TargetChannelIds")}, @EditOptions_PrependText, @EditOptions_AppendText,
                    @EditOptions_RemoveSourceForwardHeader, @EditOptions_RemoveLinks, @EditOptions_StripFormatting, @EditOptions_CustomFooter,
                    @EditOptions_DropAuthor, @EditOptions_DropMediaCaptions, @EditOptions_NoForwards, {J("@FilterOptions_AllowedMessageTypes")},
                    {J("@FilterOptions_AllowedMimeTypes")}, @FilterOptions_ContainsText, @FilterOptions_ContainsTextIsRegex,
                    @FilterOptions_ContainsTextRegexOptions, {J("@FilterOptions_AllowedSenderUserIds")}, {J("@FilterOptions_BlockedSenderUserIds")},
                    @FilterOptions_IgnoreEditedMessages, @FilterOptions_IgnoreServiceMessages, @FilterOptions_MinMessageLength, @FilterOptions_MaxMessageLength
                );";

            string insertReplacementsSql = $@"
                INSERT INTO {T("ForwardingRuleTextReplacements")} ({Q("ForwardingRuleName")}, {Q("Find")}, {Q("ReplaceWith")}, {Q("IsRegex")}, {Q("RegexOptions")})
                VALUES (@ForwardingRuleName, @Find, @ReplaceWith, @IsRegex, @RegexOptions);";

            await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

                _ = await connection.ExecuteAsync(insertRuleSql, CreateRuleParameters(rule), transaction);

                if (rule.EditOptions.TextReplacements?.Any() == true)
                {
                    var replacementParams = rule.EditOptions.TextReplacements.Select(r => new
                    {
                        ForwardingRuleName = rule.RuleName,
                        r.Find,
                        r.ReplaceWith,
                        r.IsRegex,
                        RegexOptions = (int)r.RegexOptions
                    });
                    _ = await connection.ExecuteAsync(insertReplacementsSql, replacementParams, transaction);
                }

                await transaction.CommitAsync(cancellationToken);
            });
        }


        /// <summary>
        /// Updates an existing forwarding rule asynchronously.
        /// The save operation is protected by the Polly retry policy.
        /// </summary>
        /// <param name="rule">The forwarding rule to update.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous operation.</param>
        public async Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(rule);

            string updateRuleSql = $@"
                UPDATE {T("ForwardingRules")} SET
                    {Q("IsEnabled")} = @IsEnabled, {Q("SourceChannelId")} = @SourceChannelId, {Q("TargetChannelIds")} = {J("@TargetChannelIds")},
                    {Q("EditOptions_PrependText")} = @EditOptions_PrependText, {Q("EditOptions_AppendText")} = @EditOptions_AppendText,
                    {Q("EditOptions_RemoveSourceForwardHeader")} = @EditOptions_RemoveSourceForwardHeader, {Q("EditOptions_RemoveLinks")} = @EditOptions_RemoveLinks,
                    {Q("EditOptions_StripFormatting")} = @EditOptions_StripFormatting, {Q("EditOptions_CustomFooter")} = @EditOptions_CustomFooter,
                    {Q("EditOptions_DropAuthor")} = @EditOptions_DropAuthor, {Q("EditOptions_DropMediaCaptions")} = @EditOptions_DropMediaCaptions,
                    {Q("EditOptions_NoForwards")} = @EditOptions_NoForwards, {Q("FilterOptions_AllowedMessageTypes")} = {J("@FilterOptions_AllowedMessageTypes")},
                    {Q("FilterOptions_AllowedMimeTypes")} = {J("@FilterOptions_AllowedMimeTypes")}, {Q("FilterOptions_ContainsText")} = @FilterOptions_ContainsText,
                    {Q("FilterOptions_ContainsTextIsRegex")} = @FilterOptions_ContainsTextIsRegex, {Q("FilterOptions_ContainsTextRegexOptions")} = @FilterOptions_ContainsTextRegexOptions,
                    {Q("FilterOptions_AllowedSenderUserIds")} = {J("@FilterOptions_AllowedSenderUserIds")}, {Q("FilterOptions_BlockedSenderUserIds")} = {J("@FilterOptions_BlockedSenderUserIds")},
                    {Q("FilterOptions_IgnoreEditedMessages")} = @FilterOptions_IgnoreEditedMessages, {Q("FilterOptions_IgnoreServiceMessages")} = @FilterOptions_IgnoreServiceMessages,
                    {Q("FilterOptions_MinMessageLength")} = @FilterOptions_MinMessageLength, {Q("FilterOptions_MaxMessageLength")} = @FilterOptions_MaxMessageLength
                WHERE {Q("RuleName")} = @RuleName;";

            string deleteReplacementsSql = $@"DELETE FROM {T("ForwardingRuleTextReplacements")} WHERE {Q("ForwardingRuleName")} = @RuleName;";
            string insertReplacementsSql = $@"INSERT INTO {T("ForwardingRuleTextReplacements")} ({Q("ForwardingRuleName")}, {Q("Find")}, {Q("ReplaceWith")}, {Q("IsRegex")}, {Q("RegexOptions")}) VALUES (@ForwardingRuleName, @Find, @ReplaceWith, @IsRegex, @RegexOptions);";

            await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

                int rowsAffected = await connection.ExecuteAsync(updateRuleSql, CreateRuleParameters(rule), transaction);
                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException($"Concurrency conflict: Rule '{rule.RuleName}' not found for update.");
                }

                _ = await connection.ExecuteAsync(deleteReplacementsSql, new { rule.RuleName }, transaction);

                if (rule.EditOptions.TextReplacements?.Any() == true)
                {
                    var replacementParams = rule.EditOptions.TextReplacements.Select(r => new
                    {
                        ForwardingRuleName = rule.RuleName,
                        r.Find,
                        r.ReplaceWith,
                        r.IsRegex,
                        RegexOptions = (int)r.RegexOptions
                    });
                    _ = await connection.ExecuteAsync(insertReplacementsSql, replacementParams, transaction);
                }

                await transaction.CommitAsync(cancellationToken);
            });
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
                return;
            }

            // The DDL specifies ON DELETE CASCADE, so we only need to delete the parent rule.
            // This is atomic and handled by the database.
            string deleteSql = $@"DELETE FROM {T("ForwardingRules")} WHERE {Q("RuleName")} = @RuleName;";

            await _retryPolicy.ExecuteAsync(async () =>
            {
                await using DbConnection connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                int rowsAffected = await connection.ExecuteAsync(
                    new CommandDefinition(deleteSql, new { RuleName = ruleName }, cancellationToken: cancellationToken));
                if (rowsAffected == 0)
                {
                    _logger.LogWarning("Delete operation for RuleName {RuleName} did not affect any rows, it may have already been deleted.", ruleName);
                }
            });
        }
    }
    #endregion
}
