namespace DiagManTestApp.Services;

/// <summary>
/// Order processing service with an intentional deadlock bug.
/// BUG: Two locks are acquired in different orders in different methods,
/// causing a classic ABBA deadlock scenario.
///
/// ProcessOrder acquires _inventoryLock then _orderLock
/// UpdateInventory acquires _orderLock then _inventoryLock
///
/// When these methods are called concurrently, deadlock occurs.
/// DiagMan should identify:
/// 1. Requests timing out / hanging
/// 2. High CPU usage with no progress
/// 3. The bug in this file: lock ordering inconsistency
/// </summary>
public class OrderProcessingService
{
    // BUG: These two locks are acquired in different orders in different methods
    private static readonly object _inventoryLock = new();
    private static readonly object _orderLock = new();

    private readonly Dictionary<string, int> _inventory = new();
    private readonly Dictionary<string, Order> _orders = new();
    private readonly ILogger<OrderProcessingService> _logger;

    private static int _deadlockCount = 0;
    private static int _successfulOrders = 0;

    public OrderProcessingService(ILogger<OrderProcessingService> logger)
    {
        _logger = logger;

        // Initialize some inventory
        _inventory["ITEM-001"] = 100;
        _inventory["ITEM-002"] = 50;
        _inventory["ITEM-003"] = 200;
    }

    /// <summary>
    /// Process an order by reserving inventory.
    /// BUG: Acquires locks in order: _inventoryLock -> _orderLock
    /// </summary>
    public async Task<OrderResult> ProcessOrderAsync(string orderId, string itemId, int quantity)
    {
        _logger.LogInformation(
            "Processing order {OrderId} for {Quantity}x {ItemId}",
            orderId, quantity, itemId);

        // BUG: Lock order is _inventoryLock first, then _orderLock
        // This conflicts with UpdateInventoryFromOrder which does the opposite
        lock (_inventoryLock)
        {
            _logger.LogDebug("Order {OrderId}: Acquired inventory lock, waiting for order lock...", orderId);

            // Simulate some processing time that increases deadlock probability
            Thread.Sleep(Random.Shared.Next(10, 50));

            lock (_orderLock)
            {
                _logger.LogDebug("Order {OrderId}: Acquired both locks", orderId);

                // Check inventory
                if (!_inventory.TryGetValue(itemId, out var available) || available < quantity)
                {
                    _logger.LogWarning(
                        "Order {OrderId}: Insufficient inventory. Available: {Available}, Requested: {Quantity}",
                        orderId, available, quantity);

                    return new OrderResult
                    {
                        Success = false,
                        OrderId = orderId,
                        Message = $"Insufficient inventory. Available: {available}"
                    };
                }

                // Reserve inventory
                _inventory[itemId] -= quantity;

                // Create order
                var order = new Order
                {
                    Id = orderId,
                    ItemId = itemId,
                    Quantity = quantity,
                    Status = "Confirmed",
                    CreatedAt = DateTime.UtcNow
                };
                _orders[orderId] = order;

                Interlocked.Increment(ref _successfulOrders);

                _logger.LogInformation(
                    "Order {OrderId} confirmed. Remaining inventory for {ItemId}: {Remaining}",
                    orderId, itemId, _inventory[itemId]);

                return new OrderResult
                {
                    Success = true,
                    OrderId = orderId,
                    Message = "Order confirmed"
                };
            }
        }
    }

    /// <summary>
    /// Update inventory from an external source while also updating related orders.
    /// BUG: Acquires locks in order: _orderLock -> _inventoryLock (opposite of ProcessOrder!)
    /// </summary>
    public async Task UpdateInventoryFromOrderAsync(string itemId, int quantityToAdd, string reason)
    {
        _logger.LogInformation(
            "Updating inventory for {ItemId}: +{Quantity} ({Reason})",
            itemId, quantityToAdd, reason);

        // BUG: Lock order is _orderLock first, then _inventoryLock
        // This is the OPPOSITE order from ProcessOrderAsync, causing deadlock!
        lock (_orderLock)
        {
            _logger.LogDebug("Inventory update: Acquired order lock, waiting for inventory lock...");

            // Simulate processing time
            Thread.Sleep(Random.Shared.Next(10, 50));

            lock (_inventoryLock)
            {
                _logger.LogDebug("Inventory update: Acquired both locks");

                // Update inventory
                if (!_inventory.ContainsKey(itemId))
                {
                    _inventory[itemId] = 0;
                }
                _inventory[itemId] += quantityToAdd;

                // Update any pending orders that can now be fulfilled
                foreach (var order in _orders.Values.Where(o => o.ItemId == itemId && o.Status == "Pending"))
                {
                    if (_inventory[itemId] >= order.Quantity)
                    {
                        _inventory[itemId] -= order.Quantity;
                        order.Status = "Confirmed";
                        _logger.LogInformation("Order {OrderId} now confirmed from inventory update", order.Id);
                    }
                }

                _logger.LogInformation(
                    "Inventory updated for {ItemId}. New quantity: {Quantity}",
                    itemId, _inventory[itemId]);
            }
        }
    }

    /// <summary>
    /// Simulates concurrent operations that will cause deadlock.
    /// </summary>
    public async Task SimulateConcurrentOperationsAsync()
    {
        _logger.LogInformation("Starting deadlock simulation with concurrent operations");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tasks = new List<Task>();

        // Start order processing tasks
        for (int i = 0; i < 5; i++)
        {
            var orderId = $"ORD-{Guid.NewGuid():N}"[..12];
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessOrderAsync(orderId, "ITEM-001", 1);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Order processing failed");
                }
            }));
        }

        // Start inventory update tasks (these will conflict with order processing)
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await UpdateInventoryFromOrderAsync("ITEM-001", 10, "Restock");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inventory update failed");
                }
            }));
        }

        try
        {
            // Wait with timeout - if we hit timeout, we likely have a deadlock
            var completed = await Task.WhenAny(
                Task.WhenAll(tasks),
                Task.Delay(Timeout.Infinite, cts.Token)
            );

            if (cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref _deadlockCount);
                _logger.LogError(
                    "DEADLOCK DETECTED! Operations did not complete within timeout. " +
                    "Total deadlocks: {Count}", _deadlockCount);
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _deadlockCount);
            _logger.LogError("Deadlock timeout reached. Count: {Count}", _deadlockCount);
        }
    }

    public (int Successful, int Deadlocks) GetStats() => (_successfulOrders, _deadlockCount);
}

public class Order
{
    public string Id { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }
}

public class OrderResult
{
    public bool Success { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
