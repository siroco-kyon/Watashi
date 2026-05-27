using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Watashi.Shared.Models;

namespace Watashi.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ExecutionNode> ExecutionNodes => Set<ExecutionNode>();
    public DbSet<CifsHost> CifsHosts => Set<CifsHost>();
    public DbSet<CifsShare> CifsShares => Set<CifsShare>();
    public DbSet<PermissionTemplate> PermissionTemplates => Set<PermissionTemplate>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<PermissionBundle> PermissionBundles => Set<PermissionBundle>();
    public DbSet<PermissionBundleEntry> PermissionBundleEntries => Set<PermissionBundleEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var dateTimeConverter = new ValueConverter<DateTime, string>(
            v => v.ToUniversalTime().ToString("o"),
            v => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime());

        var nullableDateTimeConverter = new ValueConverter<DateTime?, string?>(
            v => v.HasValue ? v.Value.ToUniversalTime().ToString("o") : null,
            v => v != null ? DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime() : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(dateTimeConverter);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(nullableDateTimeConverter);
            }
        }

        modelBuilder.Entity<User>(b =>
        {
            b.HasIndex(u => u.Username).IsUnique();
            b.Property(u => u.Username).IsRequired();
            b.Property(u => u.PasswordHash).IsRequired();
        });

        modelBuilder.Entity<SystemSetting>(b =>
        {
            b.HasKey(s => s.Key);
            b.Property(s => s.Value).IsRequired();
            b.HasOne<User>().WithMany().HasForeignKey(s => s.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TrustedDevice>(b =>
        {
            b.HasIndex(d => d.UserId).IsUnique();
            b.Property(d => d.MachineName).IsRequired();
            b.Property(d => d.WindowsUsername).IsRequired();
            b.Property(d => d.DeviceTokenHash).IsRequired();
            b.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasIndex(r => r.UserId);
            b.Property(r => r.TokenHash).IsRequired();
            b.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(r => r.Device).WithMany().HasForeignKey(r => r.DeviceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExecutionNode>(b =>
        {
            b.Property(n => n.Name).IsRequired();
            b.Property(n => n.NodeType).IsRequired();
            b.Property(n => n.HealthStatus).HasDefaultValue("Unknown").IsRequired();
            b.Property(n => n.IsActive).HasDefaultValue(true);
            b.Property(n => n.MaxConcurrency).HasDefaultValue(20);
            b.HasOne(n => n.GatewayNode)
                .WithMany()
                .HasForeignKey(n => n.GatewayNodeId)
                .OnDelete(DeleteBehavior.Restrict);
            b.ToTable(t => t.HasCheckConstraint("CK_ExecutionNode_NodeType", "NodeType IN ('Direct', 'Agent')"));
            b.ToTable(t => t.HasCheckConstraint("CK_ExecutionNode_HealthStatus", "HealthStatus IN ('Healthy', 'Unhealthy', 'Unknown')"));
        });

        modelBuilder.Entity<CifsHost>(b =>
        {
            b.Property(h => h.Name).IsRequired();
            b.Property(h => h.HostAddress).IsRequired();
            b.Property(h => h.Port).HasDefaultValue(445);
            b.Property(h => h.CredUsername).IsRequired();
            b.Property(h => h.CredPasswordEnc).IsRequired();
            b.HasOne(h => h.ExecutionNode).WithMany().HasForeignKey(h => h.ExecutionNodeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CifsShare>(b =>
        {
            b.HasIndex(s => new { s.HostId, s.ShareName }).IsUnique();
            b.Property(s => s.ShareName).IsRequired();
            b.Property(s => s.DisplayName).IsRequired();
            b.HasOne(s => s.Host).WithMany().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PermissionTemplate>(b =>
        {
            b.HasIndex(t => t.Name).IsUnique();
            b.Property(t => t.Name).IsRequired();
        });

        modelBuilder.Entity<UserPermission>(b =>
        {
            b.HasIndex(p => new { p.UserId, p.ShareId });
            b.Property(p => p.AllowedPath).IsRequired();
            b.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Share).WithMany().HasForeignKey(p => p.ShareId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Template).WithMany().HasForeignKey(p => p.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(p => p.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PermissionBundle>(b =>
        {
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.Name).IsRequired();
            b.HasMany(x => x.Entries).WithOne(e => e.Bundle).HasForeignKey(e => e.BundleId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PermissionBundleEntry>(b =>
        {
            b.Property(x => x.AllowedPath).IsRequired();
            b.HasOne(x => x.Share).WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.HasIndex(l => l.Timestamp).IsDescending();
            b.HasIndex(l => l.UserId);
            b.HasIndex(l => l.HostId);
            b.Property(l => l.Username).IsRequired();
            b.Property(l => l.Operation).IsRequired();
            b.Property(l => l.Result).IsRequired();
            b.ToTable(t => t.HasCheckConstraint("CK_AuditLog_Result", "Result IN ('success', 'failure')"));
        });
    }
}
