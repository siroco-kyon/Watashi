using Microsoft.EntityFrameworkCore;

namespace Watashi.Agent.Data;

public class AgentDbContext : DbContext
{
    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    public DbSet<PendingLog> PendingLogs => Set<PendingLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingLog>(b =>
        {
            b.Property(p => p.LogJson).IsRequired();
        });
    }
}

public class PendingLog
{
    public long Id { get; set; }
    public string LogJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int AttemptCount { get; set; }
}
