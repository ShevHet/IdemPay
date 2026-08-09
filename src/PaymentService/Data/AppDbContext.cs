using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace PaymentService.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationEvent> OperationEvents => Set<OperationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Operation>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.OperationId).IsUnique();
            e.Property(o => o.Amount).HasColumnType("TEXT");
        });

        modelBuilder.Entity<OperationEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.HasIndex(ev => new { ev.OperationId, ev.EventId }).IsUnique();
            e.HasOne(ev => ev.Operation)
             .WithMany(o => o.Events)
             .HasForeignKey(ev => ev.OperationId)
             .HasPrincipalKey(o => o.OperationId);
        });
    }
}
