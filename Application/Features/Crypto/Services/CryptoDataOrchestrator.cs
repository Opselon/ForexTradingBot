// -----------------
// FINAL CORRECTED VERSION
// -----------------
using Application.Common.Interfaces;
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

        public async Task<Result<UnifiedCryptoDto>> GetCryptoDetailsAsync(string coinGeckoId, CancellationToken cancellationToken)
        {
            var cacheKey = $"CryptoDetails_Unified_{coinGeckoId}";
            if (_cache.TryGetValue(cacheKey, out var cachedDetails) && cachedDetails is UnifiedCryptoDto dto) { return Result<UnifiedCryptoDto>.Success(dto); }

            var coinGeckoTask = _coinGeckoService.GetCryptoDetailsAsync(coinGeckoId, cancellationToken);
            var fmpSymbol = _symbolMapper.GetFmpSymbol(coinGeckoId);
            var fmpTask = fmpSymbol != null
                ? _fmpService.GetCryptoDetailsAsync(fmpSymbol, cancellationToken)
                : Task.FromResult(Result<FmpQuoteDto>.Failure("No FMP symbol mapping available."));

            await Task.WhenAll(coinGeckoTask, fmpTask);

            var coinGeckoResult = await coinGeckoTask;
            var fmpResult = await fmpTask;

            if (!coinGeckoResult.Succeeded && !fmpResult.Succeeded) { return Result<UnifiedCryptoDto>.Failure("Both primary and backup data sources failed."); }

            var mergedDto = new UnifiedCryptoDto { Id = coinGeckoId };
            if (coinGeckoResult.Succeeded && coinGeckoResult.Data != null)
            {
                var cg = coinGeckoResult.Data;
                mergedDto.Symbol = cg.Symbol;
                mergedDto.Name = cg.Name;

                // --- FIX: Initialize all local variables before use ---
                string? desc = null;
                double? cgPrice = null;
                double? cgMarketCap = null;
                double? high24 = null;
                double? low24 = null;
                double? vol24 = null;

                cg.Description?.TryGetValue("en", out desc);
                mergedDto.Description = desc;

                cg.MarketData?.CurrentPrice?.TryGetValue("usd", out cgPrice);
                mergedDto.Price = (decimal?)cgPrice;

                mergedDto.Change24hPercentage = (decimal?)cg.MarketData?.PriceChangePercentage24h;

                cg.MarketData?.MarketCap?.TryGetValue("usd", out cgMarketCap);
                mergedDto.MarketCap = (long?)cgMarketCap;

                cg.MarketData?.High24h?.TryGetValue("usd", out high24);
                mergedDto.DayHigh = (decimal?)high24;

                cg.MarketData?.Low24h?.TryGetValue("usd", out low24);
                mergedDto.DayLow = (decimal?)low24;

                cg.MarketData?.TotalVolume?.TryGetValue("usd", out vol24);
                mergedDto.TotalVolume = (long?)vol24;

                mergedDto.PriceDataSource = "CoinGecko";
                mergedDto.IsDataStale = false;
            }

            if (fmpResult.Succeeded && fmpResult.Data != null)
            {
                var fmp = fmpResult.Data;
                mergedDto.Symbol = fmp.Symbol.Contains("USD") ? fmp.Symbol.Replace("USD", "") : fmp.Symbol;
                mergedDto.Name ??= fmp.Name;
                mergedDto.Price = fmp.Price;
                mergedDto.Change24hPercentage = fmp.ChangesPercentage;
                mergedDto.DayHigh = fmp.DayHigh;
                mergedDto.DayLow = fmp.DayLow;
                mergedDto.MarketCap ??= fmp.MarketCap;
                mergedDto.TotalVolume ??= fmp.Volume;
                mergedDto.PriceDataSource = mergedDto.PriceDataSource == "CoinGecko" ? "CoinGecko/FMP" : "FMP";
                mergedDto.IsDataStale = false;
            }

            if (mergedDto.IsDataStale) { return Result<UnifiedCryptoDto>.Failure("Could not retrieve any data for the specified asset."); }

            _cache.Set(cacheKey, mergedDto, TimeSpan.FromMinutes(5));
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