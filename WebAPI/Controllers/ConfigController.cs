using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ForexTradingBot.Cli.Secrets;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Shared.Security; // For SecureExceptionSanitizer
using StackExchange.Redis;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/config")]
    [Authorize(Roles = "Admin")]
    public class ConfigController : ControllerBase
    {
        #region Fields and Constructor
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConfigController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public ConfigController(
            IConfiguration configuration,
            ILogger<ConfigController> logger,
            IHttpClientFactory httpClientFactory)
        // IDiagnosticsService diagnosticsService) // Option
        {
            _configuration = configuration;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }
        #endregion

        #region Security Helper Methods
        /// <summary>
        /// Redacts sensitive data for secure logging using the same patterns as ExceptionSanitizer.
        /// </summary>
        /// <param name="sensitiveData">The sensitive data to redact</param>
        /// <returns>Redacted string safe for logging</returns>
        private static string RedactSensitiveData(string? sensitiveData)
        {
            if (string.IsNullOrWhiteSpace(sensitiveData))
            {
                return "[EMPTY_DATA]";
            }

            try
            {
                string redacted = sensitiveData;

                // Apply the same redaction patterns as ExceptionSanitizer
                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"(?:password|pwd)\s*=\s*[^;\s]+", "[PASSWORD_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"(?:user\s*id|uid|username)\s*=\s*[^;\s]+", "[USERNAME_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"(?:server|host|data\s*source)\s*=\s*[^;\s]+", "[SERVER_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"(?:database|initial\s*catalog)\s*=\s*[^;\s]+", "[DATABASE_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"(?:token|secret)\s*=\s*[a-zA-Z0-9\-_]{20,}", "[TOKEN_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"[0-9]+:[a-zA-Z0-9\-_]{35}", "[BOT_TOKEN_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
                    @"[a-zA-Z0-9\-_]{20,}", "[API_KEY_REDACTED]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Ensure encryption is applied after redaction
                redacted = EncryptData(redacted);

                return redacted;
            }
            catch
            {
                return "[REDACTION_FAILED]";
            }
        }

        // Utility method to encrypt data using ProtectedData (Windows-only; on other
        // platforms ProtectedData.Protect throws, which the catch below handles).
        [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Guarded by OperatingSystem.IsWindows(); the catch-all handles non-Windows platforms where the API throws.")]
        private static string EncryptData(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return string.Empty;
            }

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return "[ENCRYPTION_UNSUPPORTED_PLATFORM]";
                }

                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
                byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Convert.ToBase64String(encrypted);
            }
            catch
            {
                return "[ENCRYPTION_FAILED]";
            }
        }

        private IActionResult CreateSecureErrorResponse(int statusCode, string userMessage, string? internalErrorId = null)
        {
            var response = new
            {
                Message = userMessage,
                ErrorId = internalErrorId ?? Guid.NewGuid().ToString("N")[..8],
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            return StatusCode(statusCode, response);
        }
        #endregion

        #region Security Validation Methods
        /// <summary>
        /// Creates a secure error response that doesn't expose sensitive information.
        /// </summary>
        /// <returns>Secure error response</returns>
        #endregion




        #region Request/Response Models
        public class TestConfigRequestModel
        {
            [Required]
            public string DbConn { get; set; } = string.Empty;

            public string DatabaseProvider { get; set; } = "postgres";

            public string? BotToken { get; set; }

            public string? RedisConn { get; set; } // Optional
        }

        public class TestConfigResponseModel
        {
            public string DatabaseStatus { get; set; } = "Not Tested";
            public string? DatabaseError { get; set; }
            public string RedisStatus { get; set; } = "Not Tested";
            public string? RedisError { get; set; }
            public string TelegramStatus { get; set; } = "Not Tested";
            public string? TelegramError { get; set; }
            public string? BotUsername { get; set; }
        }

        public class SaveConfigRequestModel
        {
            [Required]
            public string DbConn { get; set; } = string.Empty;

            public string DatabaseProvider { get; set; } = "postgres";

            public string? BotToken { get; set; }

            public string? RedisConn { get; set; }
        }
        #endregion

        #region Validation Methods
        /// <summary>
        /// Validates a database connection string using the selected provider dialect.
        /// </summary>
        private string? ValidateDatabaseConnectionString(string? connectionString, string? providerName)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            try
            {
                return NormalizeProvider(providerName) switch
                {
                    "postgres" => new NpgsqlConnectionStringBuilder(connectionString).ConnectionString,
                    "sqlserver" => new SqlConnectionStringBuilder(connectionString).ConnectionString,
                    "sqlite" => new SqliteConnectionStringBuilder(connectionString).ConnectionString,
                    _ => null
                };
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                _logger.LogWarning(
                    "Database connection validation failed. ErrorId={ErrorId}",
                    Guid.NewGuid().ToString("N")[..8]);
                return null;
            }
        }

        private static string NormalizeProvider(string? providerName)
        {
            return providerName?.Trim().ToLowerInvariant() switch
            {
                "postgres" or "postgresql" or "npgsql" => "postgres",
                "sqlserver" or "mssql" or "sql" => "sqlserver",
                "sqlite" or "sqlite3" => "sqlite",
                _ => throw new NotSupportedException("Unsupported database provider.")
            };
        }

        private static System.Data.Common.DbConnection CreateProbeConnection(
            string providerName,
            string connectionString)
        {
            return NormalizeProvider(providerName) switch
            {
                "postgres" => new NpgsqlConnection(connectionString),
                "sqlserver" => new SqlConnection(connectionString),
                "sqlite" => new SqliteConnection(connectionString),
                _ => throw new NotSupportedException("Unsupported database provider.")
            };
        }

        /// <summary>
        /// Validates and sanitizes Redis connection string.
        /// </summary>
        /// <param name="connectionString">The connection string to validate</param>
        /// <returns>Validated connection string or null if invalid</returns>
        private string? ValidateRedisConnectionString(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            try
            {
                // Use ConfigurationOptions.Parse to validate the Redis connection string format.
                ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);

                if (options.EndPoints.Count == 0)
                {
                    _logger.LogWarning("Redis connection string validation failed: No endpoints specified. Input: {EncryptedInput}", RedactSensitiveData(connectionString));
                    return null;
                }

                _logger.LogInformation("Redis connection string validation successful. Input: {EncryptedInput}", RedactSensitiveData(connectionString));
                // Return the original string as Parse does not provide a rebuilt one, but we have validated its structure.
                return connectionString;
            }
            catch (Exception ex)
            {
                string encryptedException = SecureExceptionSanitizer.SanitizeForDatabase(ex);
                string encryptedInput = RedactSensitiveData(connectionString);
                _logger.LogError("Redis connection string validation failed. Input: {EncryptedInput}. Details: {EncryptedException}", encryptedInput, encryptedException);
                return null;
            }
        }
        #endregion

        #region API Endpoints
        [HttpPost("test")]
        [ProducesResponseType(typeof(TestConfigResponseModel), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> TestConfiguration([FromBody] TestConfigRequestModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            TestConfigResponseModel response = new();

            // Test Database Connection
            try
            {
                // SECURITY: Validate and sanitize the connection string before use
                string provider = NormalizeProvider(model.DatabaseProvider);
                string? validatedDbConn = ValidateDatabaseConnectionString(model.DbConn, provider);
                if (validatedDbConn == null)
                {
                    response.DatabaseStatus = "Error";
                    response.DatabaseError = "Invalid database connection string format.";
                    _logger.LogWarning("Database connection test skipped due to invalid connection string.");
                }
                else
                {
                    _logger.LogInformation("Testing validated database connection.");
                    await using System.Data.Common.DbConnection connection = CreateProbeConnection(provider, validatedDbConn);
                    await connection.OpenAsync();
                    await connection.CloseAsync();
                    response.DatabaseStatus = "OK";
                    _logger.LogInformation("Database connection test successful.");
                }
            }
            catch (Exception ex)
            {
                string encryptedException = SecureExceptionSanitizer.SanitizeForDatabase(ex);
                string errorId = Guid.NewGuid().ToString("N")[..8];
                _logger.LogError("Database connection test failed. ErrorId: {ErrorId}. Details: {EncryptedException}", errorId, encryptedException);
                response.DatabaseStatus = "Error";
                response.DatabaseError = "Database connection failed. Please check your connection string and network connectivity.";
            }

            // Test Redis Connection
            if (!string.IsNullOrWhiteSpace(model.RedisConn))
            {
                try
                {
                    // SECURITY: Validate and sanitize the connection string before use
                    string? validatedRedisConn = ValidateRedisConnectionString(model.RedisConn);
                    if (validatedRedisConn == null)
                    {
                        response.RedisStatus = "Error";
                        response.RedisError = "Invalid Redis connection string format.";
                        _logger.LogWarning("Redis connection test skipped due to invalid connection string.");
                    }
                    else
                    {
                        _logger.LogInformation("Testing validated Redis connection.");
                        ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(validatedRedisConn);
                        if (redis.IsConnected)
                        {
                            _ = await redis.GetDatabase().PingAsync();
                            response.RedisStatus = "OK";
                            _logger.LogInformation("Redis connection test successful.");
                        }
                        else
                        {
                            response.RedisStatus = "Error";
                            response.RedisError = "Failed to connect to Redis.";
                            _logger.LogWarning("Redis connection test failed: Not connected.");
                        }
                        await redis.CloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    string encryptedException = SecureExceptionSanitizer.SanitizeForDatabase(ex);
                    string errorId = Guid.NewGuid().ToString("N")[..8];
                    _logger.LogError("Redis connection test failed. ErrorId: {ErrorId}. Details: {EncryptedException}", errorId, encryptedException);
                    response.RedisStatus = "Error";
                    response.RedisError = "Redis connection failed. Please check your connection string and network connectivity.";
                }
            }
            else
            {
                response.RedisStatus = "Not Provided";
            }

            // Test Telegram Bot Token
            try
            {
                // SECURITY: Validate bot token format
                if (string.IsNullOrWhiteSpace(model.BotToken) || !model.BotToken.Contains(':'))
                {
                    response.TelegramStatus = "Error";
                    response.TelegramError = "Invalid bot token format. Bot token should be in format: <bot_id>:<token>";
                    _logger.LogWarning("Telegram bot token validation failed: Invalid format.");
                }
                else
                {
                    _logger.LogInformation("Testing Telegram Bot Token.");
                    HttpClient client = _httpClientFactory.CreateClient();
                    HttpResponseMessage telegramApiResponse = await client.GetAsync($"https://api.telegram.org/bot{model.BotToken}/getMe");

                    if (telegramApiResponse.IsSuccessStatusCode)
                    {
                        string content = await telegramApiResponse.Content.ReadAsStringAsync();
                        JsonDocument jsonDoc = JsonDocument.Parse(content);
                        if (jsonDoc.RootElement.TryGetProperty("result", out JsonElement resultElement) &&
                            resultElement.TryGetProperty("username", out JsonElement usernameElement))
                        {
                            response.BotUsername = usernameElement.GetString();
                        }
                        response.TelegramStatus = "OK";
                        string encryptedBotUsername = EncryptData(response.BotUsername ?? string.Empty);
                        _logger.LogInformation("Telegram Bot Token test successful. Bot Username: {EncryptedBotUsername}", encryptedBotUsername);
                    }
                    else
                    {
                        string errorContent = await telegramApiResponse.Content.ReadAsStringAsync();
                        string encryptedErrorContent = EncryptData(errorContent);
                        string errorId = Guid.NewGuid().ToString("N")[..8];
                        _logger.LogWarning("Telegram Bot Token test failed. Status: {StatusCode}, Response: {EncryptedErrorContent}, ErrorId: {ErrorId}",
                            telegramApiResponse.StatusCode, encryptedErrorContent, errorId);
                        response.TelegramStatus = "Error";
                        response.TelegramError = "Telegram API test failed. Please check your bot token.";
                    }
                }
            }
            catch (Exception ex)
            {
                string encryptedException = SecureExceptionSanitizer.SanitizeForDatabase(ex);
                string errorId = Guid.NewGuid().ToString("N")[..8];
                _logger.LogError("Telegram Bot Token test failed. ErrorId: {ErrorId}. Details: {EncryptedException}", errorId, encryptedException);
                response.TelegramStatus = "Error";
                response.TelegramError = "Telegram bot token test failed. Please check your token and network connectivity.";
            }

            return Ok(response);
        }

        [HttpPost("save")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult SaveConfiguration([FromBody] SaveConfigRequestModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // SECURITY: Validate all connection strings before any processing
            string provider;
            try
            {
                provider = NormalizeProvider(model.DatabaseProvider);
            }
            catch (NotSupportedException)
            {
                return CreateSecureErrorResponse(StatusCodes.Status400BadRequest, "Unsupported database provider.");
            }

            string? validatedDbConn = ValidateDatabaseConnectionString(model.DbConn, provider);
            string? validatedRedisConn = ValidateRedisConnectionString(model.RedisConn);

            if (!string.IsNullOrWhiteSpace(model.BotToken) && !model.BotToken.Contains(':'))
            {
                return CreateSecureErrorResponse(
                    StatusCodes.Status400BadRequest,
                    "Invalid bot token format.");
            }

            if (!string.IsNullOrWhiteSpace(model.RedisConn) && validatedRedisConn == null)
            {
                return CreateSecureErrorResponse(
                    StatusCodes.Status400BadRequest,
                    "Invalid Redis connection string format.");
            }

            if (validatedDbConn == null)
            {
                return CreateSecureErrorResponse(
                    StatusCodes.Status400BadRequest,
                    "Invalid database connection string format.");
            }

            // Save secrets only to the local encrypted vault. The process
            // configuration is updated immediately; database/provider changes
            // require a restart because EF/Dapper services are initialized at boot.
            _configuration["DatabaseSettings:DatabaseProvider"] = provider;
            SecretVaultBootstrap.Set("DATABASE_PROVIDER", provider);
            SecretVaultBootstrap.Set("DATABASE_CONNECTION", validatedDbConn);

            if (!string.IsNullOrWhiteSpace(validatedRedisConn))
            {
                _configuration["ConnectionStrings:Redis"] = validatedRedisConn;
                SecretVaultBootstrap.Set("REDIS_CONNECTION", validatedRedisConn);
            }

            if (!string.IsNullOrWhiteSpace(model.BotToken))
            {
                _configuration["TelegramPanel:BotToken"] = model.BotToken;
                SecretVaultBootstrap.Set("TELEGRAM_BOT_TOKEN", model.BotToken);
            }

            _logger.LogInformation(
                "Local runtime configuration saved. Provider={Provider}, RedisConfigured={RedisConfigured}, TelegramConfigured={TelegramConfigured}.",
                provider,
                !string.IsNullOrWhiteSpace(validatedRedisConn),
                !string.IsNullOrWhiteSpace(model.BotToken));

            return Ok(new
            {
                Message = "Configuration saved to the local encrypted vault. Restart the application to apply database/provider changes.",
                Status = "Saved",
                DatabaseProvider = provider,
                RedisConfigured = !string.IsNullOrWhiteSpace(validatedRedisConn),
                TelegramConfigured = !string.IsNullOrWhiteSpace(model.BotToken),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });
        }

        #endregion
    }
}
