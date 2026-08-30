namespace NSLabs.EFCore.Extensions.Samples.Models;

public enum OrderStatus
{
    Pending = 0,
    Processing = 1,
    Shipped = 2,
    Delivered = 3,
    Cancelled = 4
}

public class Order
{
    public int Id { get; set; }
    
    public string OrderNumber { get; set; } = string.Empty;
    
    public int CustomerId { get; set; }
    
    public Customer Customer { get; set; } = null!;
    
    public DateTime OrderDate { get; set; }
    
    public OrderStatus Status { get; set; }
    
    public decimal TotalAmount { get; set; }
    
    public string? ShippingAddress { get; set; }
    
    public DateTime? ShippedDate { get; set; }
    
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
