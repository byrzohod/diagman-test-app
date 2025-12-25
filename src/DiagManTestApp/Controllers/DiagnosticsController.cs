using DiagManTestApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagManTestApp.Controllers;

/// <summary>
/// Controller for triggering various diagnostic scenarios.
/// Each endpoint triggers a specific bug pattern that DiagMan should detect.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly CacheService _cacheService;
    private readonly OrderProcessingService _orderService;
    private readonly ExternalApiService _externalApiService;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        CacheService cacheService,
        OrderProcessingService orderService,
        ExternalApiService externalApiService,
        ILogger<DiagnosticsController> logger)
    {
        _cacheService = cacheService;
        _orderService = orderService;
        _externalApiService = externalApiService;
        _logger = logger;
    }

    /// <summary>
    /// Trigger the memory leak scenario.
    /// POST /api/diagnostics/memory-leak?iterations=10
    /// </summary>
    [HttpPost("memory-leak")]
    public async Task<IActionResult> TriggerMemoryLeak([FromQuery] int iterations = 10)
    {
        _logger.LogInformation("Triggering memory leak scenario with {Iterations} iterations", iterations);

        for (int i = 0; i < iterations; i++)
        {
            await _cacheService.ProcessLargeDataSetAsync($"leak-test-{i}");
        }

        var cacheSize = _cacheService.GetApproximateCacheSize();
        var entryCount = _cacheService.GetCacheEntryCount();

        return Ok(new
        {
            message = "Memory leak scenario triggered",
            cacheEntries = entryCount,
            approximateCacheSizeMB = cacheSize / 1024.0 / 1024.0,
            warning = "Cache entries are never evicted - memory will grow unbounded!"
        });
    }

    /// <summary>
    /// Trigger the deadlock scenario.
    /// POST /api/diagnostics/deadlock
    /// </summary>
    [HttpPost("deadlock")]
    public async Task<IActionResult> TriggerDeadlock()
    {
        _logger.LogInformation("Triggering deadlock scenario");

        await _orderService.SimulateConcurrentOperationsAsync();

        var stats = _orderService.GetStats();

        return Ok(new
        {
            message = "Deadlock scenario triggered",
            successfulOrders = stats.Successful,
            deadlockCount = stats.Deadlocks,
            warning = "Lock ordering inconsistency causes ABBA deadlock!"
        });
    }

    /// <summary>
    /// Trigger the external API timeout cascade.
    /// POST /api/diagnostics/timeout-cascade?resourceCount=20
    /// </summary>
    [HttpPost("timeout-cascade")]
    public async Task<IActionResult> TriggerTimeoutCascade([FromQuery] int resourceCount = 20)
    {
        _logger.LogInformation("Triggering timeout cascade with {Count} resources", resourceCount);

        await _externalApiService.SimulateBatchOperationAsync(resourceCount);

        var stats = _externalApiService.GetStats();

        return Ok(new
        {
            message = "Timeout cascade scenario triggered",
            pendingRequests = stats.Pending,
            failedRequests = stats.Failed,
            timeouts = stats.Timeouts,
            warning = "No circuit breaker - cascading failures will overwhelm the system!"
        });
    }

    /// <summary>
    /// Get current stats for all scenarios.
    /// GET /api/diagnostics/stats
    /// </summary>
    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var cacheStats = new
        {
            entries = _cacheService.GetCacheEntryCount(),
            sizeMB = _cacheService.GetApproximateCacheSize() / 1024.0 / 1024.0
        };

        var orderStats = _orderService.GetStats();
        var apiStats = _externalApiService.GetStats();

        return Ok(new
        {
            cache = cacheStats,
            orders = new
            {
                successful = orderStats.Successful,
                deadlocks = orderStats.Deadlocks
            },
            externalApi = new
            {
                pending = apiStats.Pending,
                failed = apiStats.Failed,
                timeouts = apiStats.Timeouts
            }
        });
    }

    /// <summary>
    /// Run all scenarios in sequence.
    /// POST /api/diagnostics/run-all
    /// </summary>
    [HttpPost("run-all")]
    public async Task<IActionResult> RunAllScenarios()
    {
        _logger.LogInformation("Running all diagnostic scenarios");

        var results = new List<object>();

        // Memory leak
        await _cacheService.ProcessLargeDataSetAsync("all-scenarios-memory");
        results.Add(new { scenario = "memory-leak", triggered = true });

        // Deadlock
        await _orderService.SimulateConcurrentOperationsAsync();
        results.Add(new { scenario = "deadlock", triggered = true });

        // Timeout cascade (small count to avoid overwhelming)
        await _externalApiService.SimulateBatchOperationAsync(5);
        results.Add(new { scenario = "timeout-cascade", triggered = true });

        return Ok(new
        {
            message = "All scenarios triggered",
            results
        });
    }
}
