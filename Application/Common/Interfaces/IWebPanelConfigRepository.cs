using Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Application.Common.Interfaces
{
    public interface IWebPanelConfigRepository
    {
        /// <summary>
        /// Gets a specific configuration by its ID.
        /// </summary>
        /// <param name="id">The ID of the configuration.</param>
        /// <returns>The configuration entity if found; otherwise, null.</returns>
        Task<WebPanelConfig?> GetByIdAsync(int id);

        /// <summary>
        /// Gets the currently active web panel configuration.
        /// Assumes there is a mechanism (e.g., an 'IsActive' flag) to determine the active config.
        /// </summary>
        /// <returns>The active configuration entity if found; otherwise, null.</returns>
        Task<WebPanelConfig?> GetActiveConfigAsync();

        /// <summary>
        /// Gets all web panel configurations.
        /// </summary>
        /// <returns>A collection of all configuration entities.</returns>
        Task<IEnumerable<WebPanelConfig>> GetAllAsync();

        /// <summary>
        /// Adds a new web panel configuration.
        /// </summary>
        /// <param name="config">The configuration entity to add.</param>
        Task AddAsync(WebPanelConfig config);

        /// <summary>
        /// Updates an existing web panel configuration.
        /// </summary>
        /// <param name="config">The configuration entity to update.</param>
        Task UpdateAsync(WebPanelConfig config);

        /// <summary>
        /// Deletes a web panel configuration by its ID.
        /// </summary>
        /// <param name="id">The ID of the configuration to delete.</param>
        Task DeleteAsync(int id);
    }
}
