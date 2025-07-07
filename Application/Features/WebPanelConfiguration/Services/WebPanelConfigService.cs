using Application.Common.Interfaces;
using Application.Features.WebPanelConfiguration.DTOs;
using Application.Features.WebPanelConfiguration.Interfaces;
using AutoMapper;
using Domain.Entities;
using Microsoft.Extensions.Logging; // For logging
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Application.Features.WebPanelConfiguration.Services
{
    public class WebPanelConfigService : IWebPanelConfigService
    {
        private readonly IWebPanelConfigRepository _repository;
        private readonly IMapper _mapper;
        private readonly ILogger<WebPanelConfigService> _logger;

        public WebPanelConfigService(
            IWebPanelConfigRepository repository,
            IMapper mapper,
            ILogger<WebPanelConfigService> logger)
        {
            _repository = repository;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<WebPanelConfigDto?> GetActiveConfigAsync()
        {
            _logger.LogInformation("Fetching active web panel configuration.");
            var config = await _repository.GetActiveConfigAsync();
            if (config == null)
            {
                _logger.LogWarning("No active web panel configuration found.");
                // Optionally, create a default one if none exists
                // var defaultConfig = new WebPanelConfig(); // Uses constructor defaults
                // await _repository.AddAsync(defaultConfig);
                // _logger.LogInformation("Created and returned a default web panel configuration as no active one was found.");
                // return _mapper.Map<WebPanelConfigDto>(defaultConfig);
                return null;
            }
            return _mapper.Map<WebPanelConfigDto>(config);
        }

        public async Task<WebPanelConfigDto?> GetConfigByIdAsync(int id)
        {
            _logger.LogInformation("Fetching web panel configuration with ID: {ConfigId}", id);
            var config = await _repository.GetByIdAsync(id);
            if (config == null)
            {
                _logger.LogWarning("Web panel configuration with ID: {ConfigId} not found.", id);
                return null;
            }
            return _mapper.Map<WebPanelConfigDto>(config);
        }

        public async Task<IEnumerable<WebPanelConfigDto>> GetAllConfigsAsync()
        {
            _logger.LogInformation("Fetching all web panel configurations.");
            var configs = await _repository.GetAllAsync();
            return _mapper.Map<IEnumerable<WebPanelConfigDto>>(configs);
        }

        public async Task<WebPanelConfigDto> AddConfigAsync(CreateWebPanelConfigDto createDto)
        {
            _logger.LogInformation("Adding new web panel configuration.");
            var configEntity = _mapper.Map<WebPanelConfig>(createDto);

            // The repository's AddAsync method already handles deactivating other active configs
            // if this new one is set to active.
            await _repository.AddAsync(configEntity);
            // SaveChangesAsync is called by the repository

            _logger.LogInformation("Successfully added new web panel configuration with ID: {ConfigId}", configEntity.Id);
            return _mapper.Map<WebPanelConfigDto>(configEntity);
        }

        public async Task<bool> UpdateConfigAsync(UpdateWebPanelConfigDto updateDto)
        {
            _logger.LogInformation("Updating web panel configuration with ID: {ConfigId}", updateDto.Id);
            var existingConfig = await _repository.GetByIdAsync(updateDto.Id);
            if (existingConfig == null)
            {
                _logger.LogWarning("Update failed: Web panel configuration with ID: {ConfigId} not found.", updateDto.Id);
                return false;
            }

            _mapper.Map(updateDto, existingConfig); // Apply updates from DTO to entity
            // existingConfig.LastModifiedAt is set by AutoMapper profile

            // The repository's UpdateAsync method already handles deactivating other active configs
            // if this one is set to active.
            await _repository.UpdateAsync(existingConfig);
            // SaveChangesAsync is called by the repository

            _logger.LogInformation("Successfully updated web panel configuration with ID: {ConfigId}", updateDto.Id);
            return true;
        }

        public async Task<bool> DeleteConfigAsync(int id)
        {
            _logger.LogInformation("Deleting web panel configuration with ID: {ConfigId}", id);
            var existingConfig = await _repository.GetByIdAsync(id);
            if (existingConfig == null)
            {
                _logger.LogWarning("Delete failed: Web panel configuration with ID: {ConfigId} not found.", id);
                return false;
            }

            await _repository.DeleteAsync(id);
            // SaveChangesAsync is called by the repository
            _logger.LogInformation("Successfully deleted web panel configuration with ID: {ConfigId}", id);
            return true;
        }
    }
}
