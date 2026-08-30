namespace NSLabs.EFCore.Extensions.Samples.Models;

public class Product
{
    public int Id { get; set; }
    
    public string Sku { get; set; } = string.Empty;
    
    public string Name { get; set; } = string.Empty;
    
    public decimal Price { get; set; }
    
    public int StockQuantity { get; set; }
    
    public bool IsActive { get; set; }
    
    public DateTime LastRestocked { get; set; }
    
    public string Category { get; set; } = string.Empty;
}
