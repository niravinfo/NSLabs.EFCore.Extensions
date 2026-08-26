using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Unit;

public enum OrderStatus
{
    Pending = 0,
    Shipped = 1,
    Delivered = 2
}

public class Item
{
    public int Id { get; set; }

    public string Key1 { get; set; } = "";

    public int Key2 { get; set; }

    public int Key3 { get; set; }

    public OrderStatus Status { get; set; }

    public bool Active { get; set; }

    public int? ParentId { get; set; }

    public DateTime CreatedAt { get; set; }

    public string NotMapped { get; set; } = "";
}

public class Order
{
    public string OrderNo { get; set; } = "";

    public decimal Amount { get; set; }

    public OrderStatus Status { get; set; }
}

public class Customer
{
    public int Id { get; set; }

    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Active { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }

    public DateTime Created { get; set; }
}

public abstract class Pet
{
    public int PetId { get; set; }

    public string Name { get; set; } = "";
}

public class Cat : Pet
{
    public int LivesLeft { get; set; }
}

public class Dog : Pet
{
    public string Breed { get; set; } = "";
}

public class TestDbContext : DbContext
{
    public TestDbContext()
    {
    }

    public TestDbContext(DbContextOptions<TestDbContext> options)
        : base(options)
    {
    }

    public DbSet<Item> Items => Set<Item>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer("Server=tcp:localhost,1433;Database=BulkExtensionsTest;User Id=test;Password=test;TrustServerCertificate=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).UseIdentityColumn();
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.CreatedAt).HasComputedColumnSql("GETDATE()");
            entity.Ignore(x => x.NotMapped);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(x => x.OrderNo);
            entity.Property(x => x.Status).HasConversion<int>();
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Pet>(entity =>
        {
            entity.ToTable("Pets");
            entity.HasKey(x => x.PetId);
            entity.HasDiscriminator<string>("PetType")
                .HasValue<Cat>("Cat")
                .HasValue<Dog>("Dog");
        });
    }
}
