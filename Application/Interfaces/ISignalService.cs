using Application.DTOs;

namespace Application.Interfaces
{
    public interface ISignalService
    {
        Task<SignalDto> CreateSignalAsync(CreateSignalDto createSignalDto, CancellationToken cancellationToken = default);
        Task<SignalDto?> GetSignalByIdAsync(Guid id, CancellationToken cancellationToken = default);
        Task<IEnumerable<SignalDto>> GetRecentSignalsAsync(int count, bool includeCategory = true, bool includeAnalyses = false, CancellationToken cancellationToken = default);
        Task<IEnumerable<SignalDto>> GetSignalsByCategoryAsync(Guid categoryId, bool includeAnalyses = false, CancellationToken cancellationToken = default);
        Task<IEnumerable<SignalDto>> GetSignalsBySymbolAsync(string symbol, bool includeCategory = true, bool includeAnalyses = false, CancellationToken cancellationToken = default);
        Task UpdateSignalAsync(Guid signalId, UpdateSignalDto updateSignalDto, CancellationToken cancellationToken = default); // DTO برای آپدیت سیگنال
        Task DeleteSignalAsync(Guid signalId, CancellationToken cancellationToken = default);
    }
}