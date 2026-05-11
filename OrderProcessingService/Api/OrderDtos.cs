using System.ComponentModel.DataAnnotations;
using OrderProcessingService.Domain;

namespace OrderProcessingService.Api;

public class CreateOrderRequest
{
    [Required, StringLength(128)]
    public string CustomerId { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public List<CreateOrderItem> Items { get; set; } = new();

    /// <summary>
    /// Client-declared total. The worker will recompute the authoritative total from
    /// inventory; this field is accepted for parity with the task spec.
    /// </summary>
    [Range(0, double.MaxValue)]
    public decimal TotalAmount { get; set; }
}

public class CreateOrderItem
{
    [Required, StringLength(64)]
    public string Sku { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}

public class OrderResponse
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalAmount { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public List<OrderItemResponse> Items { get; set; } = new();

    public static OrderResponse From(Order o) => new()
    {
        Id = o.Id,
        CustomerId = o.CustomerId,
        Status = o.Status.ToString(),
        TotalAmount = o.TotalAmount,
        DiscountAmount = o.DiscountAmount,
        FinalAmount = o.FinalAmount,
        FailureReason = o.FailureReason,
        CreatedAt = o.CreatedAt,
        ProcessedAt = o.ProcessedAt,
        Items = o.Items.Select(i => new OrderItemResponse
        {
            Sku = i.Sku,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice
        }).ToList()
    };
}

public class OrderItemResponse
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
