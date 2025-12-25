using DiagManTestApp.Services;

namespace DiagManTestApp.BackgroundServices;

/// <summary>
/// Background service that continuously triggers the memory leak.
/// This simulates a realistic scenario where a background job
/// causes gradual memory growth until OOM.
/// </summary>
public class MemoryLeakBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MemoryLeakBackgroundService> _logger;
    private readonly bool _enabled;
    private readonly int _intervalSeconds;

    public MemoryLeakBackgroundService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<MemoryLeakBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _enabled = configuration.GetValue<bool>("Scenarios:MemoryLeak:Enabled", false);
        _intervalSeconds = configuration.GetValue<int>("Scenarios:MemoryLeak:IntervalSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Memory leak scenario is disabled");
            return;
        }

        _logger.LogWarning(
            "Memory leak scenario ENABLED. Will allocate memory every {Interval} seconds",
            _intervalSeconds);

        int iteration = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var cacheService = scope.ServiceProvider.GetRequiredService<CacheService>();

                await cacheService.ProcessLargeDataSetAsync($"background-{iteration++}");

                var sizeMB = cacheService.GetApproximateCacheSize() / 1024.0 / 1024.0;
                _logger.LogInformation(
                    "Memory leak iteration {Iteration}: Cache size is now {SizeMB:F2} MB",
                    iteration, sizeMB);

                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in memory leak background service");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
