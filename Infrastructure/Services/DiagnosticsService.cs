using Application.DTOs.Diagnostics;
using Application.Interfaces;
using Microsoft.Extensions.Configuration; // For IConfiguration
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Npgsql; // Assuming PostgreSQL, adjust if different
// Or using Application.Common.Interfaces IDbConnectionFactory if available and preferred

// Placeholder for Telegram GetMe response
namespace Infrastructure.Services.Telegram // Create a nested namespace for clarity
{
    public class TelegramUser // Simplified GetMe response
    {
        public long id { get; set; }
        public bool is_bot { get; set; }
        public string? first_name { get; set; }
        public string? username { get; set; }
    }

    public class TelegramApiResponse<T> // Simplified Telegram API response structure
    {
        public bool ok { get; set; }
        public T? result { get; set; }
        public string? description { get; set; } // For errors
    }
}


namespace Infrastructure.Services
{
    public class DiagnosticsService : IDiagnosticsService
    {
        private readonly IConfiguration _configuration; // To get connection string
        private readonly Data.DbProviderService _providerService; // Canonical provider resolution
        private readonly ISettingsService _settingsService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DiagnosticsService> _logger;

        public DiagnosticsService(
            IConfiguration configuration,
            Data.DbProviderService providerService,
            ISettingsService settingsService,
            IHttpClientFactory httpClientFactory,
            ILogger<DiagnosticsService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ConnectivityStatusDto> CheckConnectivityAsync(CancellationToken cancellationToken = default)
        {
            var status = new ConnectivityStatusDto();

            // 1. Check Database Connectivity. The provider lives under
            // DatabaseSettings:DatabaseProvider (see DbProviderService) — the old code read
            // the bare "DatabaseProvider" key, which never exists, so the check reported
            // "Unknown" and then probed with NpgsqlConnection regardless of provider.
            status.DatabaseProvider = _providerService.Provider.ToString();
            if (_providerService.Provider == Data.DatabaseProvider.Unsupported)
            {
                status.DatabaseProvider = "Unknown (Not configured in DatabaseSettings:DatabaseProvider)";
            }

            try
            {
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    status.CanConnectToDatabase = false;
                    status.DatabaseError = "DefaultConnection string is not configured.";
                }
                else
                {
                    await using System.Data.Common.DbConnection connection = _providerService.Provider switch
                    {
                        Data.DatabaseProvider.Postgres => new Npgsql.NpgsqlConnection(connectionString),
                        Data.DatabaseProvider.SQLite => new Microsoft.Data.Sqlite.SqliteConnection(connectionString),
                        Data.DatabaseProvider.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
                        _ => throw new NotSupportedException($"Database provider '{_providerService.Provider}' is not supported by the connectivity check.")
                    };
                    await connection.OpenAsync(cancellationToken);
                    status.CanConnectToDatabase = connection.State == System.Data.ConnectionState.Open;
                    if (status.CanConnectToDatabase)
                    {
                        status.Messages.Add($"Successfully connected to database ({status.DatabaseProvider}). State: {connection.State}");
                        await connection.CloseAsync(); // Ensure connection is closed
                    }
                    else
                    {
                        status.DatabaseError = $"Failed to open connection. State: {connection.State}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database connectivity check failed.");
                status.CanConnectToDatabase = false;
                status.DatabaseError = ex.Message;
            }

            // 2. Check Telegram API Connectivity (using getMe method)
            try
            {
                var botSettings = await _settingsService.GetTelegramBotSettingsAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(botSettings.BotToken))
                {
                    status.CanAccessTelegramApi = false;
                    status.TelegramApiError = "Bot token is not configured.";
                }
                else
                {
                    var client = _httpClientFactory.CreateClient("TelegramBotApiClient"); // Named client
                    var response = await client.GetAsync($"https://api.telegram.org/bot{botSettings.BotToken}/getMe", cancellationToken);

                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken); // Read content regardless of success for logging

                    if (response.IsSuccessStatusCode)
                    {
                        var apiResponse = JsonSerializer.Deserialize<Telegram.TelegramApiResponse<Telegram.TelegramUser>>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (apiResponse != null && apiResponse.ok && apiResponse.result != null)
                        {
                            status.CanAccessTelegramApi = true;
                            status.TelegramBotUsername = apiResponse.result.username ?? "N/A";
                            status.Messages.Add($"Successfully connected to Telegram API as bot @{status.TelegramBotUsername}.");
                        }
                        else
                        {
                            status.CanAccessTelegramApi = false;
                            status.TelegramApiError = $"Telegram API returned ok=false or no result. Description: {apiResponse?.description}. Response: {responseContent}";
                            _logger.LogWarning("Telegram API getMe call was not successful: {ResponseContent}", responseContent);
                        }
                    }
                    else
                    {
                        status.CanAccessTelegramApi = false;
                        status.TelegramApiError = $"Telegram API request failed with status code: {response.StatusCode}. Response: {responseContent}";
                        _logger.LogWarning("Telegram API getMe call failed with status {StatusCode}: {ErrorContent}", response.StatusCode, responseContent);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram API connectivity check failed.");
                status.CanAccessTelegramApi = false;
                status.TelegramApiError = ex.Message;
            }

            return status;
        }
    }
}