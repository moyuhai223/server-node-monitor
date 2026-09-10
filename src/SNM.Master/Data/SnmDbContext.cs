using Microsoft.EntityFrameworkCore;
using SNM.Master.Data.Entities;

namespace SNM.Master.Data;

public sealed class SnmDbContext(DbContextOptions<SnmDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeIp> NodeIps => Set<NodeIp>();
    public DbSet<InstallToken> InstallTokens => Set<InstallToken>();
    public DbSet<Metric1m> Metrics1m => Set<Metric1m>();
    public DbSet<Metric1h> Metrics1h => Set<Metric1h>();
    public DbSet<Metric1d> Metrics1d => Set<Metric1d>();
    public DbSet<TrafficState> TrafficStates => Set<TrafficState>();
    public DbSet<TrafficDaily> TrafficDaily => Set<TrafficDaily>();
    public DbSet<TrafficMonthly> TrafficMonthly => Set<TrafficMonthly>();
    public DbSet<AlertState> AlertStates => Set<AlertState>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Node>(e =>
        {
            e.ToTable("Nodes");
            e.HasIndex(x => x.PublicName).IsUnique().HasDatabaseName("UX_Nodes_PublicName");
            e.HasIndex(x => x.AgentKey).IsUnique().HasDatabaseName("UX_Nodes_AgentKey");
            e.HasIndex(x => x.SortOrder).HasDatabaseName("IX_Nodes_SortOrder");
            e.Property(x => x.Price).HasColumnType("TEXT");
        });

        b.Entity<NodeIp>(e =>
        {
            e.ToTable("NodeIps");
            e.HasKey(x => new { x.NodeId, x.Address });
            e.HasIndex(x => x.LastSeenAt).HasDatabaseName("IX_NodeIps_LastSeenAt");
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InstallToken>(e =>
        {
            e.ToTable("InstallTokens");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureMetrics<Metric1m>(b, "Metrics1m");
        ConfigureMetrics<Metric1h>(b, "Metrics1h");
        ConfigureMetrics<Metric1d>(b, "Metrics1d");

        b.Entity<TrafficState>(e =>
        {
            e.ToTable("TrafficState");
            e.HasKey(x => x.NodeId);
            e.HasOne<Node>().WithOne().HasForeignKey<TrafficState>(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TrafficDaily>(e =>
        {
            e.ToTable("TrafficDaily");
            e.HasKey(x => new { x.NodeId, x.Date });
        });

        b.Entity<TrafficMonthly>(e =>
        {
            e.ToTable("TrafficMonthly");
            e.HasKey(x => new { x.NodeId, x.PeriodStart });
        });

        b.Entity<AlertState>(e =>
        {
            e.ToTable("AlertStates");
            e.HasKey(x => new { x.NodeId, x.Rule, x.Subject });
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AlertEvent>(e =>
        {
            e.ToTable("AlertEvents");
            e.HasIndex(x => x.StartedAt).HasDatabaseName("IX_AlertEvents_StartedAt");
            e.HasIndex(x => new { x.NodeId, x.StartedAt }).HasDatabaseName("IX_AlertEvents_NodeId_StartedAt");
            e.HasIndex(x => x.Status).HasDatabaseName("IX_AlertEvents_Status");
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<NotificationChannel>(e => e.ToTable("NotificationChannels"));

        b.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("NotificationDeliveries");
            e.HasIndex(x => x.EventId);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<AlertEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<NotificationChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Setting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
        });

        b.Entity<AdminUser>(e =>
        {
            e.ToTable("AdminUsers");
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMetrics<T>(ModelBuilder b, string table) where T : MetricBucket
    {
        b.Entity<T>(e =>
        {
            e.ToTable(table);
            e.HasKey(x => new { x.NodeId, x.Ts });
            e.HasIndex(x => x.Ts).HasDatabaseName($"IX_{table}_Ts");
        });
    }
}
