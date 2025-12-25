namespace DiagManTestApp.Services;

/// <summary>
/// External API service with cascading timeout issues.
/// BUG: When the external API is slow, multiple layers of retries compound
/// the problem, and there's no circuit breaker. Additionally, the timeout
/// is set on the wrong layer (per-retry instead of total operation).
///
/// This causes:
/// 1. One slow API call triggers 3 retries x 30s timeout = 90s+ total
/// 2. Multiple concurrent requests pile up
/// 3. Thread pool exhaustion
/// 4. Pod becomes unresponsive and fails health checks
///
/// DiagMan should identify:
/// 1. Readiness probe failures
/// 2. High latency patterns in logs
/// 3. The bug: no circuit breaker, timeout on wrong layer
/// </summary>
public class ExternalApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ExternalApiService> _logger;
    private readonly string _externalApiUrl;

    private static int _pendingRequests = 0;
    private static int _failedRequests = 0;
    private static int _timedOutRequests = 0;

    // BUG: Timeout is too long and applied per-request, not per-operation
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 3;

    public ExternalApiService(HttpClient httpClient, IConfiguration configuration, ILogger<ExternalApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _externalApiUrl = configuration["ExternalApi:Url"] ?? "https://api.external-service.example.com";

        // BUG: Setting timeout on HttpClient means EACH request gets 30s
        // With 3 retries, total time can be 90+ seconds
        _httpClient.Timeout = RequestTimeout;
    }

    /// <summary>
    /// Fetch data from external API with retry logic.
    /// BUG: Retries with full timeout each time, no circuit breaker
    /// </summary>
    public async Task<ExternalApiResponse> FetchDataAsync(string resourceId)
    {
        Interlocked.Increment(ref _pendingRequests);
        _logger.LogInformation(
            "Fetching resource {ResourceId} from external API. Pending requests: {Pending}",
            resourceId, _pendingRequests);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug(
                    "Attempt {Attempt}/{MaxRetries} for resource {ResourceId}",
                    attempt, MaxRetries, resourceId);

                var startTime = DateTime.UtcNow;

                // BUG: Each retry gets the full timeout, compounding the issue
                // If external API is completely down, we wait 30s x 3 = 90s
                var response = await _httpClient.GetAsync(
                    $"{_externalApiUrl}/resources/{resourceId}");

                var elapsed = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "External API responded in {ElapsedMs}ms for {ResourceId}",
                    elapsed.TotalMilliseconds, resourceId);

                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();

                Interlocked.Decrement(ref _pendingRequests);

                return new ExternalApiResponse
                {
                    Success = true,
                    Data = content,
                    ResourceId = resourceId
                };
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Interlocked.Increment(ref _timedOutRequests);
                lastException = ex;

                _logger.LogWarning(
                    "Request timed out for {ResourceId} on attempt {Attempt}. " +
                    "Total timeouts: {Timeouts}. Will retry...",
                    resourceId, attempt, _timedOutRequests);

                // BUG: No backoff, no circuit breaker - just retry immediately
                // This compounds the problem when the external API is overloaded
            }
            catch (HttpRequestException ex)
            {
                Interlocked.Increment(ref _failedRequests);
                lastException = ex;

                _logger.LogWarning(ex,
                    "HTTP error for {ResourceId} on attempt {Attempt}: {Message}",
                    resourceId, attempt, ex.Message);

                // BUG: Linear backoff that doesn't help with cascading failures
                await Task.Delay(1000 * attempt);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedRequests);
                lastException = ex;

                _logger.LogError(ex,
                    "Unexpected error for {ResourceId} on attempt {Attempt}",
                    resourceId, attempt);

                await Task.Delay(1000 * attempt);
            }
        }

        Interlocked.Decrement(ref _pendingRequests);

        _logger.LogError(lastException,
            "All {MaxRetries} attempts failed for resource {ResourceId}. " +
            "Pending: {Pending}, Failed: {Failed}, Timeouts: {Timeouts}",
            MaxRetries, resourceId, _pendingRequests, _failedRequests, _timedOutRequests);

        throw new ExternalApiException(
            $"Failed to fetch resource {resourceId} after {MaxRetries} attempts",
            lastException);
    }

    /// <summary>
    /// Simulates a batch operation that will cascade failures.
    /// </summary>
    public async Task SimulateBatchOperationAsync(int resourceCount)
    {
        _logger.LogInformation("Starting batch operation for {Count} resources", resourceCount);

        // BUG: Fire off all requests at once without any throttling
        // Combined with the retry logic, this will overwhelm the thread pool
        var tasks = Enumerable.Range(1, resourceCount)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await FetchDataAsync($"resource-{i:D4}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Batch item {Index} failed", i);
                }
            }))
            .ToList();

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "Batch operation complete. Pending: {Pending}, Failed: {Failed}, Timeouts: {Timeouts}",
            _pendingRequests, _failedRequests, _timedOutRequests);
    }

    public (int Pending, int Failed, int Timeouts) GetStats() =>
        (_pendingRequests, _failedRequests, _timedOutRequests);
}

public class ExternalApiResponse
{
    public bool Success { get; set; }
    public string Data { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
}

public class ExternalApiException : Exception
{
    public ExternalApiException(string message, Exception? innerException)
        : base(message, innerException) { }
}
