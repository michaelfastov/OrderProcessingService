using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Data;
using OrderProcessingService.Domain;
using OrderProcessingService.Observability;

namespace OrderProcessingService.Processing;

/// <summary>
/// The "business logic" that runs asynchronously per order:
///   1. Validate items against current inventory (SKU exists, stock available).
///   2. Verify the client-declared TotalAmount equals sum(UnitPrice * Quantity).
///   3. Calculate a tier discount based on the order total.
///   4. Mark the order as Processed (or Failed) and persist.
///
/// All DB writes during a single attempt are wrapped in a transaction so that
/// a mid-flight failure can roll back atomically (no partial inventory drift,
/// no leftover Processing state).
/// </summary>
public class OrderProcessor
{
    /// <summary>Cents-level tolerance to absorb harmless rounding drift between client and server.</summary>
    private const decimal TotalTolerance = 0.01m;

    private readonly AppDbContext _db;
    private readonly ILogger<OrderProcessor> _logger;

    public OrderProcessor(AppDbContext db, ILogger<OrderProcessor> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid orderId, CancellationToken ct)
    {
        var claimed = await _db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, OrderStatus.Processing), ct);

        if (claimed == 0)
        {
            _logger.LogInformation("Order {OrderId} was already claimed, completed, or failed; skipping", orderId);
            return;
        }

        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found after claim, skipping", orderId);
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Simulated work to make the async nature visible during demos.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            await ProcessOrderItemsAsync(order, ct);

            await tx.CommitAsync(ct);

            OrderMetrics.ProcessedOrders.Inc();
            _logger.LogInformation(
                "Processed order {OrderId} for customer {CustomerId}: total={Total} discount={Discount} final={Final}",
                order.Id, order.CustomerId, order.TotalAmount, order.DiscountAmount, order.FinalAmount);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            await RecordFailureAsync(orderId, ex);

            OrderMetrics.FailedOrders.Inc();
            _logger.LogError(ex, "Failed to process order {OrderId}", orderId);
            // Swallow; the failure is already persisted and counted.
        }
    }

    private async Task ProcessOrderItemsAsync(Order order, CancellationToken ct)
    {
        // 1: validate against inventory (read-only, untracked) BEFORE mutating any state.
        var skus = order.Items.Select(i => i.Sku).Distinct().ToList();
        var inventory = await _db.Inventory
            .AsNoTracking()
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

        // 2: verify the client-declared total matches sum(UnitPrice * Quantity).
        var computedTotal = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        if (Math.Abs(computedTotal - order.TotalAmount) > TotalTolerance)
        {
            throw new InvalidOperationException(
                $"TotalAmount mismatch: declared={order.TotalAmount}, computed={computedTotal}");
        }

        // 3: atomically decrement inventory stock for each item.
        // The stock guard in the WHERE clause makes overselling impossible at the DB level
        // even under concurrent workers: if another transaction depleted stock between our
        // validation read and this UPDATE, the row count comes back as 0 and we throw.
        foreach (var item in order.Items)
        {
            var rowsAffected = await _db.Inventory
                .Where(i => i.Sku == item.Sku && i.StockQuantity >= item.Quantity)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(i => i.StockQuantity, i => i.StockQuantity - item.Quantity),
                    ct);

            if (rowsAffected == 0)
                throw new InvalidOperationException(
                    $"Insufficient stock for SKU '{item.Sku}' (concurrent depletion or stock changed)");
        }

        // 4: discount tier on the verified total + mark Processed.
        var discount = CalculateDiscount(computedTotal);
        order.TotalAmount = computedTotal;
        order.DiscountAmount = discount;
        order.FinalAmount = computedTotal - discount;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Persists the Failed status on the order. Called after the main transaction
    /// has been rolled back, so we first clear the tracker (the rolled-back DB
    /// writes are gone but their in-memory dirty state would otherwise be re-saved).
    /// </summary>
    private async Task RecordFailureAsync(Guid orderId, Exception ex)
    {
        _db.ChangeTracker.Clear();

        var fresh = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, CancellationToken.None);
        if (fresh is null) return;

        fresh.Status = OrderStatus.Failed;
        fresh.FailureReason = Truncate(ex.Message, 512);
        fresh.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private static decimal CalculateDiscount(decimal total) => total switch
    {
        >= 500m => Math.Round(total * 0.10m, 2),
        >= 200m => Math.Round(total * 0.05m, 2),
        _ => 0m
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
