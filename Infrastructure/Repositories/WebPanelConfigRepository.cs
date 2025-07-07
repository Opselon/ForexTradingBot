using Application.Common.Interfaces;
using Domain.Entities;
using Infrastructure.Data; // Assuming AppDbContext is here
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Infrastructure.Repositories
{
    public class WebPanelConfigRepository : IWebPanelConfigRepository
    {
        private readonly AppDbContext _dbContext;

        public WebPanelConfigRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<WebPanelConfig?> GetByIdAsync(int id)
        {
            return await _dbContext.WebPanelConfigs.FindAsync(id);
        }

        public async Task<WebPanelConfig?> GetActiveConfigAsync()
        {
            // Assuming there's an IsActive flag and we take the first one found.
            // Or, if there should only ever be one, FirstOrDefault() is fine.
            // If multiple can be active (which is unusual for 'the' active config), this logic would need refinement.
            return await _dbContext.WebPanelConfigs
                                 .Where(c => c.IsActive)
                                 .FirstOrDefaultAsync();
        }

        public async Task<IEnumerable<WebPanelConfig>> GetAllAsync()
        {
            return await _dbContext.WebPanelConfigs.ToListAsync();
        }

        public async Task AddAsync(WebPanelConfig config)
        {
            // Ensure only one config can be active if a new active one is added
            if (config.IsActive)
            {
                var currentActiveConfigs = await _dbContext.WebPanelConfigs
                                                           .Where(c => c.IsActive)
                                                           .ToListAsync();
                foreach (var activeConfig in currentActiveConfigs)
                {
                    activeConfig.IsActive = false;
                    _dbContext.WebPanelConfigs.Update(activeConfig);
                }
            }
            await _dbContext.WebPanelConfigs.AddAsync(config);
            await _dbContext.SaveChangesAsync();
        }

        public async Task UpdateAsync(WebPanelConfig config)
        {
            // Ensure only one config can be active if this one is being set to active
            if (config.IsActive)
            {
                var otherActiveConfigs = await _dbContext.WebPanelConfigs
                                                           .Where(c => c.IsActive && c.Id != config.Id)
                                                           .ToListAsync();
                foreach (var activeConfig in otherActiveConfigs)
                {
                    activeConfig.IsActive = false;
                    _dbContext.WebPanelConfigs.Update(activeConfig);
                }
            }
            _dbContext.WebPanelConfigs.Update(config);
            await _dbContext.SaveChangesAsync();
        }

        public async Task DeleteAsync(int id)
        {
            var config = await _dbContext.WebPanelConfigs.FindAsync(id);
            if (config != null)
            {
                _dbContext.WebPanelConfigs.Remove(config);
                await _dbContext.SaveChangesAsync();
            }
        }
    }
}
