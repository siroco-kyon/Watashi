using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
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
    public DbSet<AuditOutboxEntry> AuditOutboxEntries => Set<AuditOutboxEntry>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<RemoteTrashEntry> RemoteTrashEntries => Set<RemoteTrashEntry>();

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
            // Windows のアカウント名は大文字・小文字を区別しない。照合側だけを
            // OrdinalIgnoreCase にすると KU_EM / ku_em を別ユーザーとして登録でき、
            // 同じ OS アカウントが両方を初回設定できてしまうため DB 制約も揃える。
            b.Property(u => u.Username).IsRequired().UseCollation("NOCASE");
            b.Property(u => u.DisplayName).HasMaxLength(UserDisplayNames.MaxLength);
            // 初回設定待ちでも PasswordHash には使用不能ハッシュが入るため NOT NULL を維持する
            // (PasswordSetup のコメント参照)。nullable 化はテーブル再構築を招くので行わない。
            b.Property(u => u.PasswordHash).IsRequired();
            b.Property(u => u.IsPasswordSetupPending).HasDefaultValue(false);
            b.Property(u => u.IsDisabled).HasDefaultValue(false);
            b.Property(u => u.DisabledReason).HasMaxLength(500);
            b.Property(u => u.DisabledByUsername).HasMaxLength(256);
        });

        modelBuilder.Entity<SystemSetting>(b =>
        {
            b.HasKey(s => s.Key);
            b.Property(s => s.Value).IsRequired();
            b.HasOne<User>().WithMany().HasForeignKey(s => s.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TrustedDevice>(b =>
        {
            b.HasIndex(d => d.UserId);
            b.HasIndex(d => new { d.UserId, d.MachineName, d.WindowsUsername, d.IsRevoked });
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
            b.Property(p => p.Reason).HasMaxLength(CreateUserPermissionRequest.MaxReasonLength);
            b.Property(p => p.TicketNumber).HasMaxLength(CreateUserPermissionRequest.MaxTicketNumberLength);
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
            b.HasIndex(l => l.EventId).IsUnique();
            b.HasIndex(l => l.Timestamp).IsDescending();
            b.HasIndex(l => l.UserId);
            b.HasIndex(l => l.HostId);
            b.Property(l => l.Username).IsRequired();
            b.Property(l => l.Operation).IsRequired();
            b.Property(l => l.Result).IsRequired();
            b.ToTable(t => t.HasCheckConstraint("CK_AuditLog_Result", "Result IN ('success', 'failure', 'warning')"));
        });

        modelBuilder.Entity<AuditOutboxEntry>(b =>
        {
            b.HasKey(e => e.EventId);
            b.HasIndex(e => e.NextAttemptAt);
            b.Property(e => e.PayloadJson).IsRequired();
            b.Property(e => e.LastError).HasMaxLength(1000);
        });

        modelBuilder.Entity<UploadSession>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.UserId, s.IdempotencyKeyHash }).IsUnique();
            b.HasIndex(s => new { s.Status, s.ExpiresAt });
            b.Property(s => s.TargetPath).IsRequired().HasMaxLength(4096);
            b.Property(s => s.TempPath).IsRequired().HasMaxLength(4096);
            b.Property(s => s.IdempotencyKeyHash).IsRequired().HasMaxLength(64);
            b.Property(s => s.ExpectedSha256).IsRequired().HasMaxLength(64);
            b.Property(s => s.Status).IsRequired().HasMaxLength(16);
            b.Property(s => s.ErrorCode).HasMaxLength(64);
            b.Property(s => s.CommittedETag).HasMaxLength(128);
            b.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(s => s.Share).WithMany().HasForeignKey(s => s.ShareId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_UploadSession_Status",
                    "Status IN ('active', 'committing', 'completed', 'cancelled', 'expired', 'failed')");
                t.HasCheckConstraint("CK_UploadSession_Size", "TotalSize >= 0");
                t.HasCheckConstraint("CK_UploadSession_Offset", "UploadedOffset >= 0 AND UploadedOffset <= TotalSize");
            });
        });

        modelBuilder.Entity<RemoteTrashEntry>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.Status, e.ExpiresAt });
            b.HasIndex(e => new { e.DeletedByUserId, e.Status, e.DeletedAt });
            b.HasIndex(e => new { e.ShareId, e.Status, e.DeletedAt });
            b.Property(e => e.DeletedByUsername).IsRequired().HasMaxLength(256);
            b.Property(e => e.OriginalPath).IsRequired().HasMaxLength(4096);
            b.Property(e => e.TrashPath).IsRequired().HasMaxLength(4096);
            b.Property(e => e.ItemType).IsRequired().HasMaxLength(16);
            b.Property(e => e.Status).IsRequired().HasMaxLength(16);
            b.Property(e => e.ErrorCode).HasMaxLength(64);
            b.Property(e => e.RestoredPath).HasMaxLength(4096);
            b.HasOne(e => e.DeletedByUser).WithMany()
                .HasForeignKey(e => e.DeletedByUserId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>().WithMany()
                .HasForeignKey(e => e.RestoredByUserId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>().WithMany()
                .HasForeignKey(e => e.PurgedByUserId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(e => e.Share).WithMany()
                .HasForeignKey(e => e.ShareId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_RemoteTrashEntry_Status",
                    "Status IN ('trashing', 'active', 'restoring', 'restored', 'purging', 'purged', 'failed')");
                t.HasCheckConstraint("CK_RemoteTrashEntry_Size", "SizeBytes >= 0");
            });
        });
    }
}
