using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Data;

namespace OrderProcessingService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InventoryController : ControllerBase
{
    private readonly AppDbContext _db;

    public InventoryController(AppDbContext db) => _db = db;

    /// <summary>Returns the current inventory snapshot. Useful for picking valid SKUs when testing.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await _db.Inventory
            .AsNoTracking()
            .OrderBy(i => i.Sku)
            .Select(i => new { i.Sku, i.Name, i.StockQuantity })
            .ToListAsync(ct);

        return Ok(items);
    }
}
