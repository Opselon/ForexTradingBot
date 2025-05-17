using Application.Common.Interfaces;
using Application.DTOs.CryptoPay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Results; // برای Result<T>
using Shared.Settings; // برای CryptoPaySettings
using System.Net.Http.Headers;
using System.Net.Http.Json; // برای GetFromJsonAsync, PostAsJsonAsync (نیاز به System.Net.Http.Json NuGet package)
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.ExternalServices
{
    public class CryptoPayApiClient : ICryptoPayApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly CryptoPaySettings _settings;
        private readonly ILogger<CryptoPayApiClient> _logger;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public CryptoPayApiClient(
            HttpClient httpClient, // تزریق از IHttpClientFactory
            IOptions<CryptoPaySettings> settingsOptions,
            ILogger<CryptoPayApiClient> logger)
        {
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions), "CryptoPaySettings cannot be null.");
            if (string.IsNullOrWhiteSpace(_settings.ApiToken)) // ✅ بررسی null بودن
                throw new ArgumentNullException(nameof(_settings.ApiToken), "CryptoPay API Token is not configured.");

            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl); // ✅ اطمینان از BaseUrl صحیح
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Crypto-Pay-API-Token", _settings.ApiToken); // ✅✅ اینجا هدر تنظیم می‌شود
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _jsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, // برای خواندن پاسخ‌ها
                // PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, // برای ارسال درخواست‌ها اگر لازم باشد (معمولاً با JsonPropertyName در DTO هندل می‌شود)
            };
        }

        private async Task<Result<TResponse>> SendApiRequestAsync<TResponse>(
            HttpMethod method, string endpoint, object? requestBody = null, CancellationToken cancellationToken = default)
            where TResponse : class // محدود کردن TResponse به کلاس برای اینکه بتواند null باشد
        {
            try
            {
                HttpResponseMessage response;
                var requestUri = new Uri(_httpClient.BaseAddress!, endpoint); // اطمینان از اینکه BaseAddress null نیست

                _logger.LogDebug("Sending CryptoPay API request. Method: {Method}, Endpoint: {Endpoint}, Body: {@RequestBody}",
                    method, endpoint, requestBody);

                if (method == HttpMethod.Post && requestBody != null)
                {
                    // استفاده از PostAsJsonAsync برای سادگی (نیاز به بسته NuGet System.Net.Http.Json)
                    response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, _jsonSerializerOptions, cancellationToken);
                }
                else if (method == HttpMethod.Get)
                {
                    response = await _httpClient.GetAsync(endpoint, cancellationToken);
                }
                else
                {
                    // برای سایر متدها (PUT, DELETE) می‌توانید مشابه Post عمل کنید
                    var request = new HttpRequestMessage(method, endpoint);
                    if (requestBody != null)
                    {
                        var jsonContent = JsonSerializer.Serialize(requestBody, _jsonSerializerOptions);
                        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    }
                    response = await _httpClient.SendAsync(request, cancellationToken);
                }

                _logger.LogDebug("CryptoPay API response received. Status: {StatusCode}, Endpoint: {Endpoint}", response.StatusCode, endpoint);

                if (response.IsSuccessStatusCode)
                {
                    var apiResponse = await response.Content.ReadFromJsonAsync<CryptoPayApiResponse<TResponse>>(_jsonSerializerOptions, cancellationToken);
                    if (apiResponse != null && apiResponse.Ok && apiResponse.Result != null)
                    {
                        return Result<TResponse>.Success(apiResponse.Result);
                    }
                    else if (apiResponse != null && !apiResponse.Ok && apiResponse.Error != null)
                    {
                        _logger.LogWarning("CryptoPay API call successful (HTTP Status) but returned an error. Endpoint: {Endpoint}, ErrorCode: {ErrorCode}, ErrorName: {ErrorName}",
                            endpoint, apiResponse.Error.Code, apiResponse.Error.Name);
                        return Result<TResponse>.Failure($"CryptoPay API Error: {apiResponse.Error.Name} (Code: {apiResponse.Error.Code})");
                    }
                    _logger.LogWarning("CryptoPay API call successful (HTTP Status) but response format was unexpected. Endpoint: {Endpoint}, Content: {Content}",
                        endpoint, await response.Content.ReadAsStringAsync(cancellationToken));
                    return Result<TResponse>.Failure("CryptoPay API returned an unexpected response format after a successful HTTP call.");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("CryptoPay API request failed. Endpoint: {Endpoint}, Status: {StatusCode}, Reason: {ReasonPhrase}, Content: {ErrorContent}",
                        endpoint, response.StatusCode, response.ReasonPhrase, errorContent);
                    // تلاش برای خواندن خطای ساختاریافته CryptoPay
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<CryptoPayApiResponse<object>>(errorContent, _jsonSerializerOptions);
                        if (errorResponse != null && errorResponse.Error != null)
                        {
                            return Result<TResponse>.Failure($"CryptoPay API Error (HTTP {(int)response.StatusCode}): {errorResponse.Error.Name} (Code: {errorResponse.Error.Code})");
                        }
                    }
                    catch { /*  اگر نتوانستیم خطای ساختاریافته را بخوانیم، از پیام عمومی استفاده می‌کنیم */ }
                    return Result<TResponse>.Failure($"CryptoPay API request failed with status {response.StatusCode}: {response.ReasonPhrase}");
                }
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP request exception during CryptoPay API call to {Endpoint}.", endpoint);
                return Result<TResponse>.Failure($"Network error communicating with CryptoPay API: {httpEx.Message}");
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON serialization/deserialization exception during CryptoPay API call to {Endpoint}.", endpoint);
                return Result<TResponse>.Failure($"Error processing response from CryptoPay API: {jsonEx.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("CryptoPay API call to {Endpoint} was cancelled.", endpoint);
                return Result<TResponse>.Failure("Operation was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception during CryptoPay API call to {Endpoint}.", endpoint);
                return Result<TResponse>.Failure($"An unexpected error occurred: {ex.Message}");
            }
        }

        // کلاس‌های کمکی برای خواندن پاسخ استاندارد CryptoPay API
        private class CryptoPayApiResponse<T>
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("result")]
            public T? Result { get; set; }

            [JsonPropertyName("error")]
            public CryptoPayApiError? Error { get; set; }
        }

        private class CryptoPayApiError
        {
            [JsonPropertyName("code")]
            public int Code { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }


        public async Task<Result<CryptoPayInvoiceDto>> CreateInvoiceAsync(CreateCryptoPayInvoiceRequestDto request, CancellationToken cancellationToken = default)
        {
            // اطمینان از اینکه مقادیر ضروری در request وجود دارند (اعتبارسنجی در لایه Application انجام می‌شود)
            return await SendApiRequestAsync<CryptoPayInvoiceDto>(HttpMethod.Post, "createInvoice", request, cancellationToken);
        }

        public async Task<Result<IEnumerable<CryptoPayInvoiceDto>>> GetInvoicesAsync(GetCryptoPayInvoicesRequestDto? request = null, CancellationToken cancellationToken = default)
        {
            // ساخت query string از request DTO
            string endpoint = "getInvoices";
            if (request != null)
            {
                var queryParams = new List<string>();
                if (!string.IsNullOrWhiteSpace(request.Asset)) queryParams.Add($"asset={request.Asset}");
                if (!string.IsNullOrWhiteSpace(request.InvoiceIds)) queryParams.Add($"invoice_ids={request.InvoiceIds}");
                if (!string.IsNullOrWhiteSpace(request.Status)) queryParams.Add($"status={request.Status}");
                if (request.Offset.HasValue) queryParams.Add($"offset={request.Offset.Value}");
                if (request.Count.HasValue) queryParams.Add($"count={request.Count.Value}");

                if (queryParams.Any())
                {
                    endpoint += "?" + string.Join("&", queryParams);
                }
            }
            return await SendApiRequestAsync<IEnumerable<CryptoPayInvoiceDto>>(HttpMethod.Get, endpoint, null, cancellationToken);
        }

        public async Task<Result<CryptoPayAppInfoDto>> GetMeAsync(CancellationToken cancellationToken = default)
        {
            return await SendApiRequestAsync<CryptoPayAppInfoDto>(HttpMethod.Get, "getMe", null, cancellationToken);
        }

        public async Task<Result<IEnumerable<CryptoPayBalanceDto>>> GetBalanceAsync(CancellationToken cancellationToken = default)
        {
            return await SendApiRequestAsync<IEnumerable<CryptoPayBalanceDto>>(HttpMethod.Get, "getBalance", null, cancellationToken);
        }
    }
}