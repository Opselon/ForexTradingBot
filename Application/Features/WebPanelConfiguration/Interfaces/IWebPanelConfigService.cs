using Application.Features.WebPanelConfiguration.DTOs; // Adjusted to the new DTO location
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Application.Features.WebPanelConfiguration.Interfaces
{
    /// <summary>
    /// Service interface for managing web panel configurations.
    /// It handles business logic related to configurations and uses DTOs for data transfer.
    /// </summary>
    public interface IWebPanelConfigService
    {
        /// <summary>
        /// Gets the currently active web panel configuration.
        /// </summary>
        /// <returns>A DTO representing the active configuration, or null if not found.</returns>
        Task<WebPanelConfigDto?> GetActiveConfigAsync();

        /// <summary>
        /// Gets a specific web panel configuration by its ID.
        /// </summary>
        /// <param name="id">The ID of the configuration.</param>
        /// <returns>A DTO representing the configuration, or null if not found.</returns>
        Task<WebPanelConfigDto?> GetConfigByIdAsync(int id);

        /// <summary>
        /// Gets all web panel configurations.
        /// </summary>
        /// <returns>A collection of DTOs representing all configurations.</returns>
        Task<IEnumerable<WebPanelConfigDto>> GetAllConfigsAsync();

        /// <summary>
        /// Adds a new web panel configuration.
        /// </summary>
        /// <param name="createDto">The DTO containing data for the new configuration.</param>
        /// <returns>The created configuration DTO, including its generated ID.</returns>
        Task<WebPanelConfigDto> AddConfigAsync(CreateWebPanelConfigDto createDto);

        /// <summary>
        /// Updates an existing web panel configuration.
        /// </summary>
        /// <param name="updateDto">The DTO containing updated data for the configuration.</param>
        /// <returns>True if update was successful, false otherwise.</returns>
        Task<bool> UpdateConfigAsync(UpdateWebPanelConfigDto updateDto);

        /// <summary>
        /// Deletes a web panel configuration by its ID.
        /// </summary>
        /// <param name="id">The ID of the configuration to delete.</param>
        /// <returns>True if deletion was successful, false otherwise.</returns>
        Task<bool> DeleteConfigAsync(int id);
    }
}
