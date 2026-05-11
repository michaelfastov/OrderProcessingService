using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Domain;

namespace OrderProcessingService.Data;

/// <summary>
/// Applies migrations (or EnsureCreated for the demo) and seeds inventory.
/// Called once at startup.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        // For a small demo service we use EnsureCreated; in a real project
        // this would be `await db.Database.MigrateAsync(ct);` against EF migrations.
        await db.Database.EnsureCreatedAsync(ct);

        if (!await db.Inventory.AnyAsync(ct))
        {
            logger.LogInformation("Seeding inventory");
            db.Inventory.AddRange(
                new Inventory { Id = Guid.NewGuid(), Sku = "SKU-001", Name = "Wireless Mouse",   StockQuantity = 50,  UnitPrice = 24.99m },
                new Inventory { Id = Guid.NewGuid(), Sku = "SKU-002", Name = "Mechanical Keyboard", StockQuantity = 25, UnitPrice = 89.50m },
                new Inventory { Id = Guid.NewGuid(), Sku = "SKU-003", Name = "27\" Monitor",     StockQuantity = 10,  UnitPrice = 329.00m },
                new Inventory { Id = Guid.NewGuid(), Sku = "SKU-004", Name = "USB-C Hub",         StockQuantity = 100, UnitPrice = 39.00m }
            );
            await db.SaveChangesAsync(ct);
        }
    }
}
