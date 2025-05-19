namespace BackgroundTasks
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                    }
                    
                    // Add your main processing logic here
                    await Task.Yield(); // Allow other tasks to run if needed
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while processing");
                }
            }
        }
    }
}
