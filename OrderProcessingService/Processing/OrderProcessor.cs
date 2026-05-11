using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Data;
using OrderProcessingService.Domain;
using OrderProcessingService.Observability;

namespace OrderProcessingService.Processing;

/// <summary>
/// The "business logic" that runs asynchronously per order:
///   1. Validate items against current inventory (SKU exists, stock available).
///   2. Verify the client-declared TotalAmount equals Σ (UnitPrice × Quantity).
///   3. Calculate a tier discount based on the order total.
///   4. Mark the order as Processed (or Failed) and persist.
/// </summary>
public class OrderProcessor
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderProcessor> _logger;

    public OrderProcessor(AppDbContext db, ILogger<OrderProcessor> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid orderId, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found, skipping", orderId);
            return;
        }

        if (order.Status == OrderStatus.Processed)
        {
            _logger.LogInformation("Order {OrderId} already processed, skipping (idempotent)", orderId);
            return;
        }

        order.Status = OrderStatus.Processing;
        await _db.SaveChangesAsync(ct);

        try
        {
            // Simulated work to make the async nature visible during demos.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            await ProcessOrderItemsAsync(order, ct);

            OrderMetrics.ProcessedOrders.Inc();
            _logger.LogInformation(
                "Processed order {OrderId} for customer {CustomerId}: total={Total} discount={Discount} final={Final}",
                order.Id, order.CustomerId, order.TotalAmount, order.DiscountAmount, order.FinalAmount);
        }
        catch (Exception ex)
        {
            // Drop any partially-applied tracked changes (e.g. mid-loop inventory decrements)
            // so the failure save only writes the Failed state on the order row itself.
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;

            var freshOrder = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, CancellationToken.None);
            if (freshOrder is not null)
            {
                freshOrder.Status = OrderStatus.Failed;
                freshOrder.FailureReason = Truncate(ex.Message, 512);
                freshOrder.ProcessedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(CancellationToken.None);
            }

            OrderMetrics.FailedOrders.Inc();
            _logger.LogError(ex, "Failed to process order {OrderId}", orderId);
            // Swallow — the failure is already persisted and counted.
        }
    }

    /// <summary>
    /// Cents-level tolerance to absorb harmless rounding drift between client and server.
    /// </summary>
    private const decimal TotalTolerance = 0.01m;

    private async Task ProcessOrderItemsAsync(Order order, CancellationToken ct)
    {
        // 1: validate against inventory (read-only) BEFORE mutating any state.
        var skus = order.Items.Select(i => i.Sku).Distinct().ToList();
        var inventory = await _db.Inventory
            .Where(i => skus.Contains(i.Sku))
            .ToDictionaryAsync(i => i.Sku, ct);

        foreach (var item in order.Items)
        {
            if (!inventory.TryGetValue(item.Sku, out var inv))
                throw new InvalidOperationException($"Unknown SKU '{item.Sku}'");
            if (item.Quantity <= 0)
                throw new InvalidOperationException($"Invalid quantity {item.Quantity} for SKU '{item.Sku}'");
            if (item.UnitPrice < 0m)
                throw new InvalidOperationException($"Invalid unit price {item.UnitPrice} for SKU '{item.Sku}'");
            if (inv.StockQuantity < item.Quantity)
                throw new InvalidOperationException(
                    $"Insufficient stock for SKU '{item.Sku}' (have {inv.StockQuantity}, need {item.Quantity})");
        }

        // 2: verify the client-declared total matches Σ (UnitPrice × Quantity).
        var computedTotal = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        if (Math.Abs(computedTotal - order.TotalAmount) > TotalTolerance)
        {
            throw new InvalidOperationException(
                $"TotalAmount mismatch: declared={order.TotalAmount}, computed={computedTotal}");
        }

        // 3: decrement inventory stock now that all checks have passed.
        foreach (var item in order.Items)
        {
            inventory[item.Sku].StockQuantity -= item.Quantity;
        }

        // 4: discount tier on the verified total.
        var discount = CalculateDiscount(computedTotal);
        order.TotalAmount = computedTotal;
        order.DiscountAmount = discount;
        order.FinalAmount = computedTotal - discount;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    private static decimal CalculateDiscount(decimal total) => total switch
    {
        >= 500m => Math.Round(total * 0.10m, 2),
        >= 200m => Math.Round(total * 0.05m, 2),
        _ => 0m
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
