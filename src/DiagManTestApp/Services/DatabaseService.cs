using System.Data;
using Npgsql;

namespace DiagManTestApp.Services;

/// <summary>
/// Database service with an intentional connection pool exhaustion bug.
/// BUG: Connections are opened but never properly disposed when exceptions occur.
/// The GetUserWithRetry method has a bug where it doesn't close connections
/// on failure, eventually exhausting the connection pool.
///
/// This will cause the application to hang waiting for connections.
/// DiagMan should identify:
/// 1. Increasing latency and timeouts from logs
/// 2. "Timeout expired. The timeout period elapsed prior to obtaining a connection"
/// 3. The bug in this file: connections not disposed in catch block
/// </summary>
public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private static int _activeConnections = 0;
    private static int _leakedConnections = 0;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("Database connection string not configured");
        _logger = logger;
    }

    /// <summary>
    /// Gets user data with retry logic.
    /// BUG: When an exception occurs during query execution, the connection
    /// is not properly disposed, causing connection pool exhaustion.
    /// </summary>
    public async Task<UserData?> GetUserWithRetryAsync(int userId, int maxRetries = 3)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            // BUG: Connection opened here but not disposed on exception path
            var connection = new NpgsqlConnection(_connectionString);

            try
            {
                await connection.OpenAsync();
                Interlocked.Increment(ref _activeConnections);
                _logger.LogDebug("Opened connection for user {UserId}, active: {Active}",
                    userId, _activeConnections);

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, email FROM users WHERE id = @id";
                command.Parameters.AddWithValue("@id", userId);

                // Simulate intermittent database issues
                if (Random.Shared.Next(100) < 30) // 30% chance of failure
                {
                    throw new NpgsqlException("Simulated database error: Connection reset by peer");
                }

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var user = new UserData
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Email = reader.GetString(2)
                    };

                    // Success path - properly close connection
                    await connection.CloseAsync();
                    Interlocked.Decrement(ref _activeConnections);
                    return user;
                }

                await connection.CloseAsync();
                Interlocked.Decrement(ref _activeConnections);
                return null;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "Database query failed for user {UserId}, attempt {Attempt}/{MaxRetries}",
                    userId, attempt, maxRetries);

                // BUG: Connection is NOT closed here! This is the leak!
                // The connection stays open and counts against the pool limit.
                // Correct code would be: await connection.CloseAsync();
                Interlocked.Increment(ref _leakedConnections);

                _logger.LogDebug("Connection leaked! Total leaked: {Leaked}, Active: {Active}",
                    _leakedConnections, _activeConnections);

                await Task.Delay(100 * attempt); // Backoff
            }
        }

        _logger.LogError(lastException,
            "All {MaxRetries} attempts failed for user {UserId}. Leaked connections: {Leaked}",
            maxRetries, userId, _leakedConnections);

        throw new DataException($"Failed to get user {userId} after {maxRetries} attempts", lastException);
    }

    /// <summary>
    /// Simulates high-load database operations that will eventually exhaust connections.
    /// </summary>
    public async Task SimulateHighLoadAsync(int operationCount)
    {
        _logger.LogInformation("Starting high-load simulation with {Count} operations", operationCount);

        var tasks = new List<Task>();
        for (int i = 0; i < operationCount; i++)
        {
            var userId = Random.Shared.Next(1, 1000);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await GetUserWithRetryAsync(userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "High-load operation failed for user {UserId}", userId);
                }
            }));
        }

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "High-load simulation complete. Active connections: {Active}, Leaked: {Leaked}",
            _activeConnections, _leakedConnections);
    }

    public (int Active, int Leaked) GetConnectionStats() => (_activeConnections, _leakedConnections);
}

public class UserData
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
