using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Samples.Models;

namespace NSLabs.EFCore.Extensions.Samples.Data;

public class SampleDbContext : DbContext
{
    public SampleDbContext(DbContextOptions<SampleDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    
    public DbSet<Customer> Customers => Set<Customer>();
    
    public DbSet<Order> Orders => Set<Order>();
    
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    
    public DbSet<InventoryLog> InventoryLogs => Set<InventoryLog>();

    public DbSet<DailyArticleViews> DailyArticleViews => Set<DailyArticleViews>();

    public DbSet<EnergyReading> EnergyReadings => Set<EnergyReading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Product configuration
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Sku).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Price).HasPrecision(18, 2);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.HasIndex(e => e.Sku).IsUnique();
        });

        // Customer configuration
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.Email).IsUnique();
        });

        // Order configuration
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OrderNumber).HasMaxLength(50).IsRequired();
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.HasIndex(e => e.OrderNumber).IsUnique();
            
            entity.HasOne(e => e.Customer)
                .WithMany(e => e.Orders)
                .HasForeignKey(e => e.CustomerId);
        });

        // OrderItem configuration
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.Subtotal).HasPrecision(18, 2);
            
            entity.HasOne(e => e.Order)
                .WithMany(e => e.Items)
                .HasForeignKey(e => e.OrderId);
                
            entity.HasOne(e => e.Product)
                .WithMany()
                .HasForeignKey(e => e.ProductId);
        });

        // InventoryLog configuration
        modelBuilder.Entity<InventoryLog>(entity =>
        {
            entity.ToTable("InventoryLogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.HasIndex(e => e.ProductId);
        });

        // DailyArticleViews configuration: one counter row per (article, day).
        // The composite UNIQUE index is the upsert conflict target on PG/SQLite
        // (SQL Server MERGE needs no constraint, but the index documents intent).
        modelBuilder.Entity<DailyArticleViews>(entity =>
        {
            entity.ToTable("DailyArticleViews");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ArticleId, e.Date }).IsUnique();
        });

        // EnergyReading configuration: one reading per (meter, day).
        modelBuilder.Entity<EnergyReading>(entity =>
        {
            entity.ToTable("EnergyReadings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MeterId).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => new { e.MeterId, e.Date }).IsUnique();
        });
    }
}
