namespace NSLabs.EFCore.Extensions.Samples.Models;

public class Customer
{
    public int Id { get; set; }
    
    public string Email { get; set; } = string.Empty;
    
    public string Name { get; set; } = string.Empty;
    
    public bool IsActive { get; set; }
    
    public int LoyaltyPoints { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime? LastOrderDate { get; set; }
    
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
