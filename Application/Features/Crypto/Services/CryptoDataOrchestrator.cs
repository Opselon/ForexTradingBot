// -----------------
// FINAL CORRECTED VERSION
// -----------------
using Application.Common.Interfaces;
using Application.DTOs.CoinGecko;
using Application.DTOs.Fmp;
using Application.Features.Crypto.Dtos;
using Application.Features.Crypto.Interfaces;
using Application.Features.Fmp.Interfaces;
using Microsoft.Extensions.Logging;
using Shared.Results;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Features.Crypto.Services
{
    public class CryptoDataOrchestrator : ICryptoDataOrchestrator
    {
        private readonly ICoinGeckoService _coinGeckoService;
        private readonly IFmpService _fmpService;

        private readonly ILogger<CryptoDataOrchestrator> _logger;
        private readonly IMemoryCacheService<object> _cache;
        private readonly ICryptoSymbolMapper _symbolMapper;
        public CryptoDataOrchestrator(ICoinGeckoService coinGeckoService, IFmpService fmpService, ILogger<CryptoDataOrchestrator> logger, IMemoryCacheService<object> cache, ICryptoSymbolMapper symbolMapper)
        {
            _coinGeckoService = coinGeckoService; _fmpService = fmpService; _logger = logger; _cache = cache;
            _symbolMapper = symbolMapper;
            _logger.LogInformation("CryptoDataOrchestrator initialized with CoinGecko and FMP services.");
            if (_symbolMapper == null) { throw new ArgumentNullException(nameof(symbolMapper), "CryptoSymbolMapper cannot be null"); }
            _logger.LogInformation("CryptoSymbolMapper initialized successfully.");
            if (_cache == null) { throw new ArgumentNullException(nameof(cache), "MemoryCacheService cannot be null"); }
            _logger.LogInformation("MemoryCacheService initialized successfully.");
            _logger.LogInformation("CryptoDataOrchestrator dependencies injected successfully.");
            _logger.LogInformation("CryptoDataOrchestrator ready to handle requests.");
        }

        public async Task<Result<List<UnifiedCryptoDto>>> GetCryptoListAsync(int page, int perPage, CancellationToken cancellationToken)
        {
            var cacheKey = $"CryptoList_Page{page}";
            if (_cache.TryGetValue(cacheKey, out var cachedList) && cachedList is List<UnifiedCryptoDto> list) { return Result<List<UnifiedCryptoDto>>.Success(list); }

            var coinGeckoResult = await _coinGeckoService.GetCoinMarketsAsync(page, perPage, cancellationToken);
            if (!coinGeckoResult.Succeeded || coinGeckoResult.Data == null)
            {
                _logger.LogWarning("Orchestrator: CoinGecko list fetch failed. Attempting FMP fallback.");
                var fmpResult = await _fmpService.GetTopCryptosAsync(20, cancellationToken);
                if (fmpResult.Succeeded && fmpResult.Data != null)
                {
                    var fmpList = fmpResult.Data.Select(c => new UnifiedCryptoDto { Id = c.Symbol, Symbol = c.Symbol.Contains("USD") ? c.Symbol.Replace("USD", "") : c.Symbol, Name = c.Name, Price = c.Price, Change24hPercentage = c.ChangesPercentage, PriceDataSource = "FMP" }).ToList();
                    _cache.Set(cacheKey, fmpList, TimeSpan.FromMinutes(2));
                    return Result<List<UnifiedCryptoDto>>.Success(fmpList);
                }
                return Result<List<UnifiedCryptoDto>>.Failure(coinGeckoResult.Errors.Concat(fmpResult.Errors).ToList());
            }

            var unifiedList = coinGeckoResult.Data.Select(c => new UnifiedCryptoDto { Id = c.Id, Symbol = c.Symbol, Name = c.Name, ImageUrl = c.Image, MarketCapRank = c.MarketCapRank, Price = (decimal?)c.CurrentPrice, Change24hPercentage = (decimal?)c.PriceChangePercentage24h, MarketCap = c.MarketCap, PriceDataSource = "CoinGecko", IsDataStale = false }).ToList();
            _cache.Set(cacheKey, unifiedList, TimeSpan.FromMinutes(2));
            return Result<List<UnifiedCryptoDto>>.Success(unifiedList);
        }

        public async Task<Result<UnifiedCryptoDto>> GetCryptoDetailsAsync(string coinSymbol, CancellationToken cancellationToken)
        {
            // FIX: Use the coin symbol in the cache key, as the UI requests by symbol.
            var cacheKey = $"CryptoDetails_Unified_{coinSymbol}";
            if (_cache.TryGetValue(cacheKey, out var cachedDetails) && cachedDetails is UnifiedCryptoDto dto)
            {
                _logger.LogDebug("Serving crypto details for symbol '{CoinSymbol}' from cache.", coinSymbol);
                return Result<UnifiedCryptoDto>.Success(dto);
            }

            _logger.LogInformation("Cache MISS for coin details: {CoinSymbol}. Fetching from API.", coinSymbol);

            // --- FIX: Mapping Symbol to Provider IDs ---

            // 1. Get CoinGecko ID from Symbol
            string? coinGeckoProviderId = null;
            // FIX: Ensure the type parameter for Result is correct (CoinGeckoCoinDetailsDto)
            Result<CoinDetailsDto> coinGeckoMappingResult; // FIX: Use correct DTO type
            try
            {
                // Assuming your _symbolMapper has a method to get the CoinGecko ID from the symbol.
                // This is the critical mapping that was missing or failing.
                // FIX: Call the GetCoinGeckoId method - Make sure your ICryptoSymbolMapper interface has this method!
                coinGeckoProviderId = _symbolMapper.GetCoinGeckoId(coinSymbol); // <-- Call the correct method on the mapper

                if (string.IsNullOrEmpty(coinGeckoProviderId))
                {
                    // Mapper didn't throw, but returned no ID for CoinGecko for this symbol.
                    // FIX: Ensure the type parameter for Failure is correct (CoinGeckoCoinDetailsDto)
                    coinGeckoMappingResult = Result<CoinDetailsDto>.Failure($"No CoinGecko mapping available for symbol '{coinSymbol}'."); // FIX: Use correct DTO type
                    _logger.LogWarning("No CoinGecko mapping available for symbol '{CoinSymbol}'.", coinSymbol);
                }
                else
                {
                    // Mapping successful, prepare to fetch. Data is null for now, it comes from the task.
                    // FIX: Ensure the type parameter for Success is correct (CoinGeckoCoinDetailsDto)
                    coinGeckoMappingResult = Result<CoinDetailsDto>.Success(null); // FIX: Use correct DTO type
                }
            }
            catch (Exception ex)
            {
                // Mapping failed unexpectedly (e.g., symbol not in mapper's internal dictionary).
                _logger.LogError(ex, "Mapping symbol '{CoinSymbol}' to CoinGecko ID failed.", coinSymbol);
                // FIX: Ensure the type parameter for Failure is correct (CoinGeckoCoinDetailsDto)
                coinGeckoMappingResult = Result<CoinDetailsDto>.Failure($"Mapping to CoinGecko ID failed for symbol '{coinSymbol}': {ex.Message}"); // FIX: Use correct DTO type
            }


            // 2. Prepare CoinGecko fetch task based on mapping result
            // FIX: Ensure the type parameter for the Task is correct (CoinGeckoCoinDetailsDto)
            Task<Result<CoinDetailsDto>> coinGeckoTask; // FIX: Use correct DTO type
            if (coinGeckoMappingResult.Succeeded && !string.IsNullOrEmpty(coinGeckoProviderId))
            {
                // Mapping worked and gave a valid ID, perform the actual fetch using that ID.
                _logger.LogInformation("Requesting coin details for ID '{CoinGeckoId}' (symbol '{CoinSymbol}') from CoinGecko API.", coinGeckoProviderId, coinSymbol);
                // FIX: Call the actual CoinGecko service method with the mapped ID
                coinGeckoTask = _coinGeckoService.GetCryptoDetailsAsync(coinGeckoProviderId, cancellationToken); // <-- Use the MAPPED CoinGecko ID!
            }
            else
            {
                // Mapping failed or returned no ID. The CoinGecko task is effectively a failure.
                // Create a completed task with the mapping failure result.
                // FIX: Ensure the type parameter for Task.FromResult is correct (CoinGeckoCoinDetailsDto)
                coinGeckoTask = Task.FromResult(coinGeckoMappingResult); // FIX: Use correct DTO type
            }

            // 3. Prepare FMP fetch task
            // Use the input coinSymbol to get the FMP symbol.
            var fmpSymbol = _symbolMapper.GetFmpSymbol(coinSymbol); // <-- Use the input symbol!
            var fmpTask = fmpSymbol != null
                ? _fmpService.GetCryptoDetailsAsync(fmpSymbol, cancellationToken)
                : Task.FromResult(Result<FmpQuoteDto>.Failure($"No FMP symbol mapping available for '{coinSymbol}'."));


            // --- Wait for tasks ---
            await Task.WhenAll(coinGeckoTask, fmpTask);

            var coinGeckoResult = await coinGeckoTask; // Will contain the fetch result OR the mapping failure result
            var fmpResult = await fmpTask;

            // Check if AT LEAST one source succeeded
            if (!coinGeckoResult.Succeeded && !fmpResult.Succeeded)
            {
                // Log the specific errors from each source
                _logger.LogError("Both primary (CoinGecko) and backup (FMP) data sources failed for symbol '{CoinSymbol}'. CG Error: {CgError}. FMP Error: {FmpError}.",
                                 coinSymbol, coinGeckoResult.Errors.FirstOrDefault(), fmpResult.Errors.FirstOrDefault());
                return Result<UnifiedCryptoDto>.Failure("Could not retrieve data for the specified asset from any available source.");
            }

            // --- Merge Results ---
            var mergedDto = new UnifiedCryptoDto
            {
                // FIX: Store the original symbol in the Id field, as the UI uses this for subsequent calls
                Id = coinSymbol,
                // Initialize properties to default/null
                Symbol = coinSymbol, // Start with the requested symbol
                Name = coinSymbol,   // Default name to symbol
                PriceDataSource = "N/A",
                IsDataStale = true, // Assume stale until data is merged in
                                    // Initialize other nullable value types to null explicitly for clarity
                Price = null,
                Change24hPercentage = null,
                MarketCap = null,
                TotalVolume = null,
                DayHigh = null,
                DayLow = null,
                Description = null,
                // ... initialize other properties ...
            };


            // Check if CoinGecko data is available before accessing it
            if (coinGeckoResult.Succeeded && coinGeckoResult.Data != null) // FIX: Add null check for Data
            {
                var cg = coinGeckoResult.Data;

                // Use CG data
                mergedDto.Symbol = cg.Symbol ?? coinSymbol; // Prefer CG symbol, fallback to requested symbol
                mergedDto.Name = cg.Name ?? mergedDto.Symbol; // Prefer CG name, fallback to symbol

                // Safely get description (CoinGecko description is a dictionary)
                string? desc = null;
                cg.Description?.TryGetValue("en", out desc); // Assuming 'en' is the desired language key
                mergedDto.Description = desc;

                // Safely get market data values (CoinGecko market data values are dictionaries)
                double? cgPrice = null;
                cg.MarketData?.CurrentPrice?.TryGetValue("usd", out cgPrice);
                mergedDto.Price = (decimal?)cgPrice;

                mergedDto.Change24hPercentage = (decimal?)cg.MarketData?.PriceChangePercentage24h;

                double? cgMarketCap = null;
                cg.MarketData?.MarketCap?.TryGetValue("usd", out cgMarketCap);
                mergedDto.MarketCap = (long?)cgMarketCap; // Cast to long?

                double? high24 = null;
                cg.MarketData?.High24h?.TryGetValue("usd", out high24);
                mergedDto.DayHigh = (decimal?)high24; // Cast to decimal?

                double? low24 = null;
                cg.MarketData?.Low24h?.TryGetValue("usd", out low24);
                mergedDto.DayLow = (decimal?)low24; // Cast to decimal?

                double? vol24 = null;
                cg.MarketData?.TotalVolume?.TryGetValue("usd", out vol24);
                mergedDto.TotalVolume = (long?)vol24; // Cast to long?

                mergedDto.PriceDataSource = "CoinGecko"; // Source of primary data
                mergedDto.IsDataStale = false; // Data is fresh if CoinGecko succeeded
                                               // Add timestamp from CG if available? cg.LastUpdated
            }

            // Use FMP as fallback if CG failed or for specific missing fields
            // Check fmpResult.Succeeded *before* accessing fmpResult.Data
            if (fmpResult.Succeeded && fmpResult.Data != null) // FIX: Add null check for Data
            {
                var fmp = fmpResult.Data;

                // Overwrite/Fill in gaps from FMP if needed
                mergedDto.Symbol ??= fmp.Symbol.Contains("USD") ? fmp.Symbol.Replace("USD", "") : fmp.Symbol;
                mergedDto.Name ??= fmp.Name;
                mergedDto.Price ??= fmp.Price; // Only use FMP price if CG price was null
                mergedDto.Change24hPercentage ??= fmp.ChangesPercentage; // Only use FMP change if CG change was null
                mergedDto.DayHigh ??= fmp.DayHigh;
                mergedDto.DayLow ??= fmp.DayLow;
                mergedDto.MarketCap ??= fmp.MarketCap;
                mergedDto.TotalVolume ??= fmp.Volume;

                // Update data source indicator if FMP data was used or is primary
                if (mergedDto.PriceDataSource == "CoinGecko" && (mergedDto.Price == fmp.Price || mergedDto.Change24hPercentage == fmp.ChangesPercentage || mergedDto.MarketCap == fmp.MarketCap))
                {
                    // FMP contributed some data, indicate both sources
                    mergedDto.PriceDataSource = "CoinGecko/FMP";
                }
                else if (mergedDto.PriceDataSource == "N/A")
                {
                    // Only FMP succeeded
                    mergedDto.PriceDataSource = "FMP";
                    mergedDto.IsDataStale = false; // Data is fresh from FMP
                }
                // If CG succeeded but FMP also had data for CG fields, we still mark CG as primary
                // IsDataStale should be false if *any* source successfully contributed key data (price/change/MC)
                if (!mergedDto.IsDataStale || mergedDto.Price != null || mergedDto.Change24hPercentage != null || mergedDto.MarketCap != null)
                {
                    mergedDto.IsDataStale = false;
                }
                // Add timestamp from FMP if available? fmp.Timestamp
            }


            // Final check if we got *any* meaningful data
            if (mergedDto.IsDataStale || (mergedDto.Price == null && mergedDto.MarketCap == null && mergedDto.TotalVolume == null && mergedDto.Description == null))
            {
                _logger.LogWarning("Failed to build comprehensive data for symbol '{CoinSymbol}' from available sources.", coinSymbol);
                // Reconstruct the error message to be slightly more specific if CoinGecko mapping failed
                string initialError = coinGeckoMappingResult.Succeeded ? "Could not retrieve sufficient data" : $"CoinGecko mapping failed: {coinGeckoMappingResult.Errors.FirstOrDefault()}. Could not retrieve data";
                return Result<UnifiedCryptoDto>.Failure($"{initialError} for the specified asset from available sources.");
            }

            // Cache the successfully merged result
            _cache.Set(cacheKey, mergedDto, TimeSpan.FromMinutes(5));
            _logger.LogInformation("Successfully built and cached data for symbol '{CoinSymbol}'. Data source: {DataSource}", coinSymbol, mergedDto.PriceDataSource);

            return Result<UnifiedCryptoDto>.Success(mergedDto);
        }


        private string MapCoinGeckoIdToFmpSymbol(string coinGeckoId)
        {
            var map = new Dictionary<string, string> { { "bitcoin", "BTC" }, { "ethereum", "ETHUSD" }, { "staked-ether", "ETHUSD" }, { "tether", "USDTUSD" }, { "binancecoin", "BNBUSD" }, { "solana", "SOLUSD" }, { "ripple", "XRPUSD" }, { "cardano", "ADAUSD" }, { "dogecoin", "DOGEUSD" }, { "tron", "TRXUSD" } };
            if (map.TryGetValue(coinGeckoId.ToLower(), out var fmpSymbol)) { _logger.LogInformation("Mapped CoinGecko ID '{CoinGeckoId}' to FMP Symbol '{FmpSymbol}'", coinGeckoId, fmpSymbol); return fmpSymbol; }
            string guessedSymbol = coinGeckoId.ToUpper() + "USD";
            _logger.LogWarning("No explicit map found for CoinGecko ID '{CoinGeckoId}'. Guessing FMP Symbol as '{GuessedSymbol}'", coinGeckoId, guessedSymbol);
            return guessedSymbol;
        }
    }
}