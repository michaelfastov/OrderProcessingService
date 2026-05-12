using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Api;
using OrderProcessingService.Data;
using OrderProcessingService.Domain;
using OrderProcessingService.Messaging;
using OrderProcessingService.Observability;

namespace OrderProcessingService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IOrderPublisher _publisher;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(AppDbContext db, IOrderPublisher publisher, ILogger<OrdersController> logger)
    {
        _db = db;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// Accepts an order, persists it as <see cref="OrderStatus.Pending"/>,
    /// publishes a message for the background worker, and returns 202 Accepted.
    /// The HTTP call does NOT wait for processing to finish.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            TotalAmount = request.TotalAmount,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            Items = request.Items.Select(i => new OrderItem
            {
                Id = Guid.NewGuid(),
                Sku = i.Sku,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        _publisher.Publish(new ProcessOrderMessage(order.Id));
        OrderMetrics.ReceivedOrders.Inc();

        _logger.LogInformation("Accepted order {OrderId} for customer {CustomerId}", order.Id, order.CustomerId);

        var body = OrderResponse.From(order);
        return AcceptedAtAction(nameof(GetById), new { id = order.Id }, body);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return order is null ? NotFound() : Ok(OrderResponse.From(order));
    }

    /// <summary>
    /// Lists orders newest-first with simple paging and optional status / customer filters.
    /// </summary>
    /// <param name="limit">Page size (1–200, default 50).</param>
    /// <param name="offset">Number of rows to skip (default 0).</param>
    /// <param name="status">Optional status filter (Pending, Processing, Processed, Failed).</param>
    /// <param name="customerId">Optional exact-match customer filter.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<OrderResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] OrderStatus? status = null,
        [FromQuery] string? customerId = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        if (offset < 0) offset = 0;

        var query = _db.Orders.AsNoTracking();

        if (status is not null)
            query = query.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(customerId))
            query = query.Where(o => o.CustomerId == customerId);

        var orders = await query
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(orders.Select(OrderResponse.From));
    }
}
