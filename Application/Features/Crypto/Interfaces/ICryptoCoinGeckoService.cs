// -----------------
// NEW FILE
// -----------------
using Application.DTOs.CoinGecko;
using Shared.Results;

namespace Application.Features.Crypto.Interfaces
{
    /// <summary>
    /// Defines the application service contract for handling business logic related to
    /// cryptocurrency data obtained from the CoinGecko source.
    /// </summary>
    public interface ICoinGeckoService
    {


        /// <summary>
        /// Gets a paginated list of coins sorted by market cap.
        /// </summary>
        /// <param name="page">The page number to fetch.</param>
        /// <param name="perPage">The number of coins per page.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Result containing a list of CoinMarketDto objects.</returns>
        Task<Result<List<CoinMarketDto>>> GetCoinMarketsAsync(int page, int perPage, CancellationToken cancellationToken);

        /// <summary>
        /// Gets a list of the top trending cryptocurrencies from CoinGecko.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing a list of TrendingCoinDto objects on success.</returns>
        Task<Result<List<TrendingCoinDto>>> GetTrendingCoinsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Gets the detailed information for a single cryptocurrency by its CoinGecko ID.
        /// </summary>
        /// <param name="id">The unique ID of the cryptocurrency (e.g., "bitcoin").</param>
        /// <returns>A Result containing the detailed CoinDetailsDto for the specified crypto on success.</returns>
        Task<Result<CoinDetailsDto>> GetCryptoDetailsAsync(string id, CancellationToken cancellationToken);
    }
}