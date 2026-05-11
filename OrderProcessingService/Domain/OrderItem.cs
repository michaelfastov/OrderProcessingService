namespace OrderProcessingService.Domain;

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }

    /// <summary>Stock-keeping unit referencing <see cref="Inventory.Sku"/>.</summary>
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    public Order? Order { get; set; }
}
