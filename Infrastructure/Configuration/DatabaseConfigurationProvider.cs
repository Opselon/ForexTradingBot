using Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging; // Assuming DynamicConfigurationService uses ILogger
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Infrastructure.Configuration
{
    public class DatabaseConfigurationProvider : ConfigurationProvider, IDisposable
    {
        private readonly IDynamicConfigurationService _dynamicConfigService;
        private readonly IConfiguration _initialFileConfiguration; // To access appsettings.json for defaults
        private readonly IDisposable? _reloadTokenChange = null; // For future reload functionality

        // A simple way to trigger reload for now, could be more sophisticated
        public static event Action? RequestReload;

        public DatabaseConfigurationProvider(
            IDynamicConfigurationService dynamicConfigService,
            IConfiguration initialFileConfiguration)
        {
            _dynamicConfigService = dynamicConfigService ?? throw new ArgumentNullException(nameof(dynamicConfigService));
            _initialFileConfiguration = initialFileConfiguration ?? throw new ArgumentNullException(nameof(initialFileConfiguration));

            // Subscribe to a static event for reload requests.
            // In a more robust system, IDynamicConfigurationService might raise an event,
            // or use IOptionsMonitor with a change token source.
            RequestReload += OnReloadRequested;
        }

        private void OnReloadRequested()
        {
            // This method will be called when RequestReload event is invoked.
            // It tells the Configuration system that configuration has changed.
            // ASP.NET Core's OptionsMonitor will pick this up.
            Load(); // Reload data
            OnReload(); // Notify subscribers that configuration has changed
        }

        public static void TriggerReload()
        {
            RequestReload?.Invoke();
        }


        public override void Load()
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Get all defined setting keys from the service (which knows them from registration step)
                var definedKeys = _dynamicConfigService.GetAllDefinedSettingKeys();
                if (!definedKeys.Any())
                {
                    // This might happen if registration hasn't occurred or no settings are defined.
                    // Log this situation.
                    // _logger.LogWarning("DatabaseConfigurationProvider: No setting keys were defined in DynamicConfigurationService at Load time.");
                    return;
                }

                // For each defined key, get its effective value (Env > DB > JSON default)
                // The GetDecryptedValueAsync should handle this precedence.
                foreach (var key in definedKeys)
                {
                    // We need to run this synchronously if possible for Load(), or adapt Load to be async.
                    // Configuration providers' Load method is synchronous by default.
                    // This is a common challenge with async config sources.
                    // For simplicity here, we'll call a synchronous wrapper or use .Result/.Wait()
                    // CAUTION: Blocking on async code like this (.Result / .Wait()) can lead to deadlocks
                    // in some contexts (e.g., ASP.NET Classic request threads). In ASP.NET Core, it's generally
                    // less problematic for startup code but still not ideal.
                    // A better long-term solution involves custom async configuration loading if possible.

                    string? value = Task.Run(() => _dynamicConfigService.GetDecryptedValueAsync(key)).GetAwaiter().GetResult();

                    if (value != null) // Only add if there's a value
                    {
                        // Configuration keys use ":" as separator.
                        // Environment variables often use "__" (double underscore).
                        // The _dynamicConfigService and IConfiguration should handle this mapping internally.
                        // Here, we use the standard ":" for the Data dictionary.
                        Data[key] = value;
                    }
                }
            }
            catch (Exception)
            {
                // Log the exception. Depending on the app's needs, you might:
                // - Throw to prevent startup if config is critical.
                // - Continue with an empty Data dictionary (or defaults from appsettings).
                // _logger.LogError(ex, "Error loading configuration from database.");
                // For now, we'll let it proceed with whatever data it managed to load or an empty set.
                // This means app might fall back to appsettings.json if DB load fails.
                Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // Clear on error
            }
        }

        public void Dispose()
        {
            RequestReload -= OnReloadRequested;
            _reloadTokenChange?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
