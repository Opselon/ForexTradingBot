using Application.Common.Interfaces;
using Application.DTOs;
using Application.Interfaces;
using AutoMapper;
using Domain.Entities;
using Microsoft.Extensions.Logging; // برای لاگ کردن
// using Application.Common.Exceptions;

namespace Application.Services
{
    public class SignalService : ISignalService
    {
        private readonly ISignalRepository _signalRepository;
        private readonly ISignalCategoryRepository _categoryRepository;
        private readonly IMapper _mapper;
        private readonly IAppDbContext _context;
        private readonly ILogger<SignalService> _logger;

        public SignalService(
            ISignalRepository signalRepository,
            ISignalCategoryRepository categoryRepository,
            IMapper mapper,
            IAppDbContext context,
            ILogger<SignalService> logger)
        {
            _signalRepository = signalRepository ?? throw new ArgumentNullException(nameof(signalRepository));
            _categoryRepository = categoryRepository ?? throw new ArgumentNullException(nameof(categoryRepository));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SignalDto> CreateSignalAsync(CreateSignalDto createSignalDto, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to create a new signal for symbol {Symbol}", createSignalDto.Symbol);

            SignalCategory? category = await _categoryRepository.GetByIdAsync(createSignalDto.CategoryId, cancellationToken);
            if (category == null)
            {
                _logger.LogWarning("SignalCategory with ID {CategoryId} not found for creating signal.", createSignalDto.CategoryId);
                throw new Exception($"SignalCategory with ID {createSignalDto.CategoryId} not found."); // یا NotFoundException
            }

            Signal signal = _mapper.Map<Signal>(createSignalDto);
            signal.Id = Guid.NewGuid();
            signal.PublishedAt = DateTime.UtcNow;

            await _signalRepository.AddAsync(signal, cancellationToken);
            _ = await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Signal with ID {SignalId} created successfully for symbol {Symbol}", signal.Id, signal.Symbol);

            // برای برگرداندن DTO با جزئیات کامل (شامل Category)
            Signal? createdSignalWithDetails = await _signalRepository.GetByIdWithDetailsAsync(signal.Id, cancellationToken);
            return _mapper.Map<SignalDto>(createdSignalWithDetails);
        }

        public async Task<SignalDto?> GetSignalByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching signal with ID {SignalId}", id);
            Signal? signal = await _signalRepository.GetByIdWithDetailsAsync(id, cancellationToken); // با جزئیات برای DTO
            if (signal == null)
            {
                _logger.LogWarning("Signal with ID {SignalId} not found.", id);
                return null;
            }
            return _mapper.Map<SignalDto>(signal);
        }

        public async Task<IEnumerable<SignalDto>> GetRecentSignalsAsync(int count, bool includeCategory = true, bool includeAnalyses = false, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching {Count} recent signals. IncludeCategory: {IncludeCategory}, IncludeAnalyses: {IncludeAnalyses}", count, includeCategory, includeAnalyses);
            // پیاده‌سازی این متد در Repository باید امکان انتخاب Include ها را بدهد
            // فعلاً از متد موجود Repository استفاده می‌کنیم و در صورت نیاز آن را تغییر می‌دهیم
            IEnumerable<Signal> signals = await _signalRepository.GetRecentSignalsAsync(count, cancellationToken); // این متد باید Include ها را مدیریت کند یا متد جدیدی بسازیم
            return _mapper.Map<IEnumerable<SignalDto>>(signals);
        }

        public async Task<IEnumerable<SignalDto>> GetSignalsByCategoryAsync(Guid categoryId, bool includeAnalyses = false, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching signals for CategoryID {CategoryId}. IncludeAnalyses: {IncludeAnalyses}", categoryId, includeAnalyses);
            IEnumerable<Signal> signals = await _signalRepository.GetSignalsByCategoryIdAsync(categoryId, cancellationToken); // این متد باید Include ها را مدیریت کند
            return _mapper.Map<IEnumerable<SignalDto>>(signals);
        }

        public async Task<IEnumerable<SignalDto>> GetSignalsBySymbolAsync(string symbol, bool includeCategory = true, bool includeAnalyses = false, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching signals for Symbol {Symbol}. IncludeCategory: {IncludeCategory}, IncludeAnalyses: {IncludeAnalyses}", symbol, includeCategory, includeAnalyses);
            IEnumerable<Signal> signals = await _signalRepository.GetSignalsBySymbolAsync(symbol, cancellationToken); // این متد باید Include ها را مدیریت کند
            return _mapper.Map<IEnumerable<SignalDto>>(signals);
        }

        public async Task UpdateSignalAsync(Guid signalId, UpdateSignalDto updateSignalDto, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to update signal with ID {SignalId}", signalId);
            Signal? signal = await _signalRepository.GetByIdAsync(signalId, cancellationToken);
            if (signal == null)
            {
                _logger.LogWarning("Signal with ID {SignalId} not found for update.", signalId);
                throw new Exception($"Signal with ID {signalId} not found."); // یا NotFoundException
            }

            if (updateSignalDto.CategoryId.HasValue)
            {
                SignalCategory? category = await _categoryRepository.GetByIdAsync(updateSignalDto.CategoryId.Value, cancellationToken);
                if (category == null)
                {
                    _logger.LogWarning("SignalCategory with ID {CategoryId} not found for updating signal {SignalId}.", updateSignalDto.CategoryId.Value, signalId);
                    throw new Exception($"SignalCategory with ID {updateSignalDto.CategoryId.Value} not found.");
                }
            }

            _ = _mapper.Map(updateSignalDto, signal); // AutoMapper فیلدهای غیر null از DTO را به موجودیت مپ می‌کند
            // signal.UpdatedAt = DateTime.UtcNow; // اگر فیلد UpdatedAt در Signal دارید

            await _signalRepository.UpdateAsync(signal, cancellationToken); // یا _context.Entry(signal).State = Modified
            _ = await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Signal with ID {SignalId} updated successfully.", signalId);
        }

        public async Task DeleteSignalAsync(Guid signalId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to delete signal with ID {SignalId}", signalId);
            Signal? signal = await _signalRepository.GetByIdAsync(signalId, cancellationToken);
            if (signal == null)
            {
                _logger.LogWarning("Signal with ID {SignalId} not found for deletion.", signalId);
                throw new Exception($"Signal with ID {signalId} not found."); // یا NotFoundException
            }

            await _signalRepository.DeleteAsync(signal, cancellationToken);
            _ = await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Signal with ID {SignalId} deleted successfully.", signalId);
        }
    }
}