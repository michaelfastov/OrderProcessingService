using Microsoft.EntityFrameworkCore;
using OrderProcessingService.Domain;

namespace OrderProcessingService.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Inventory> Inventory => Set<Inventory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.CustomerId).IsRequired().HasMaxLength(128);
            e.Property(o => o.TotalAmount).HasColumnType("numeric(18,2)");
            e.Property(o => o.DiscountAmount).HasColumnType("numeric(18,2)");
            e.Property(o => o.FinalAmount).HasColumnType("numeric(18,2)");
            e.Property(o => o.Status).HasConversion<int>();
            e.Property(o => o.FailureReason).HasMaxLength(512);
            e.HasIndex(o => o.Status);
            e.HasMany(o => o.Items)
                .WithOne(i => i.Order!)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Sku).IsRequired().HasMaxLength(64);
            e.Property(i => i.UnitPrice).HasColumnType("numeric(18,2)");
        });

        modelBuilder.Entity<Inventory>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Sku).IsRequired().HasMaxLength(64);
            e.Property(i => i.Name).IsRequired().HasMaxLength(256);
            e.HasIndex(i => i.Sku).IsUnique();
        });
    }
}
