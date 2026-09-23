using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Benchmarks.Infrastructure;

public sealed class BenchItem
{
    public int Id { get; set; }

    public string Key1 { get; set; } = "";

    public int Key2 { get; set; }

    public int Key3 { get; set; }

    public bool Active { get; set; }
}

public sealed class BenchOrder
{
    public string OrderNo { get; set; } = "";

    public decimal Amount { get; set; }
}

public sealed class BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options) : DbContext(options)
{
    public DbSet<BenchItem> Items => Set<BenchItem>();

    public DbSet<BenchOrder> Orders => Set<BenchOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchItem>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<BenchOrder>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(x => x.OrderNo);
        });
    }
}
