namespace NSLabs.EFCore.Extensions.Samples.Models;

public class InventoryLog
{
    public long Id { get; set; }
    
    public int ProductId { get; set; }
    
    public string Action { get; set; } = string.Empty;
    
    public int QuantityChange { get; set; }
    
    public int NewQuantity { get; set; }
    
    public DateTime Timestamp { get; set; }
    
    public string? Notes { get; set; }
}
