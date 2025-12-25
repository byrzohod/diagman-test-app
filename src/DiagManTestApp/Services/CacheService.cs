namespace DiagManTestApp.Services;

/// <summary>
/// Cache service with an intentional memory leak.
/// BUG: The cache never evicts entries, causing unbounded memory growth.
/// The ProcessLargeDataSet method allocates large byte arrays that are stored
/// indefinitely in the _cache dictionary without any eviction policy.
///
/// This will cause OOM (Out of Memory) errors when deployed with memory limits.
/// DiagMan should identify:
/// 1. Memory growing continuously from Prometheus metrics
/// 2. OOMKilled events from container status
/// 3. The bug in this file: no cache eviction policy
/// </summary>
public class CacheService
{
    // BUG: Static dictionary grows unbounded - entries are never removed
    private static readonly Dictionary<string, CacheEntry> _cache = new();
    private static readonly object _lock = new();
    private readonly ILogger<CacheService> _logger;

    public CacheService(ILogger<CacheService> logger)
    {
        _logger = logger;
    }

    public void Set(string key, byte[] data, TimeSpan? expiration = null)
    {
        lock (_lock)
        {
            // BUG: We store expiration but never check it or evict expired entries
            _cache[key] = new CacheEntry
            {
                Data = data,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiration.HasValue ? DateTime.UtcNow.Add(expiration.Value) : null
            };

            _logger.LogDebug("Cached entry {Key}, size: {Size} bytes, total entries: {Count}",
                key, data.Length, _cache.Count);
        }
    }

    public byte[]? Get(string key)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                // BUG: We check expiration on read but don't evict - entry stays in memory
                if (entry.ExpiresAt.HasValue && entry.ExpiresAt < DateTime.UtcNow)
                {
                    _logger.LogDebug("Cache entry {Key} expired but not removing it", key);
                    return null; // Returns null but doesn't remove the entry!
                }
                return entry.Data;
            }
            return null;
        }
    }

    /// <summary>
    /// Simulates processing large datasets that get cached.
    /// Each call allocates 10MB that never gets freed.
    /// </summary>
    public async Task ProcessLargeDataSetAsync(string dataSetId)
    {
        _logger.LogInformation("Processing large dataset {DataSetId}", dataSetId);

        // Simulate processing - allocate 10MB of data
        var largeData = new byte[10 * 1024 * 1024]; // 10MB
        Random.Shared.NextBytes(largeData);

        // BUG: Cache forever with no eviction - this is the memory leak
        Set($"dataset_{dataSetId}_{Guid.NewGuid()}", largeData, TimeSpan.FromMinutes(5));

        await Task.Delay(100); // Simulate some processing time

        _logger.LogInformation("Completed processing dataset {DataSetId}, cache size: {Count} entries",
            dataSetId, _cache.Count);
    }

    public int GetCacheEntryCount() => _cache.Count;

    public long GetApproximateCacheSize()
    {
        lock (_lock)
        {
            return _cache.Values.Sum(e => e.Data.Length);
        }
    }

    private class CacheEntry
    {
        public required byte[] Data { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
    }
}
