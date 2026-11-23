using GroupDashboard.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupDashboard.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GroupEntity> Groups => Set<GroupEntity>();
    public DbSet<GroupManagerEntity> GroupManagers => Set<GroupManagerEntity>();
    public DbSet<AnalyticsEventEntity> AnalyticsEvents => Set<AnalyticsEventEntity>();
    public DbSet<DigestNotificationEntity> DigestNotifications => Set<DigestNotificationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var group = modelBuilder.Entity<GroupEntity>();
        group.ToTable("groups");
        group.HasKey(x => x.ChatId);
        group.Property(x => x.ChatId).HasColumnName("chat_id");
        group.Property(x => x.Title).HasColumnName("title").HasMaxLength(256).IsRequired();
        group.Property(x => x.Username).HasColumnName("username").HasMaxLength(64);
        group.Property(x => x.MemberCount).HasColumnName("member_count");
        group.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        group.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        var groupManager = modelBuilder.Entity<GroupManagerEntity>();
        groupManager.ToTable("group_managers");
        groupManager.HasKey(x => new { x.ChatId, x.ManagerTelegramId });
        groupManager.Property(x => x.ChatId).HasColumnName("chat_id");
        groupManager.Property(x => x.ManagerTelegramId).HasColumnName("manager_telegram_id");
        groupManager.Property(x => x.ConnectedAtUtc).HasColumnName("connected_at_utc");
        groupManager.HasIndex(x => x.ManagerTelegramId)
            .HasDatabaseName("ix_group_managers_manager_telegram_id");
        groupManager.HasOne(x => x.Group)
            .WithMany(x => x.Managers)
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        var analyticsEvent = modelBuilder.Entity<AnalyticsEventEntity>();
        analyticsEvent.ToTable("analytics_events");
        analyticsEvent.HasKey(e => e.EventId);
        analyticsEvent.Property(e => e.EventId).HasColumnName("event_id");
        analyticsEvent.Property(e => e.ChatId).HasColumnName("chat_id");
        analyticsEvent.Property(e => e.TaskId).HasColumnName("task_id");
        analyticsEvent.Property(e => e.EmployeeId).HasColumnName("employee_id");
        analyticsEvent.Property(e => e.Kind).HasColumnName("kind");
        analyticsEvent.Property(e => e.Title).HasColumnName("title");
        analyticsEvent.Property(e => e.Description).HasColumnName("description");
        analyticsEvent.Property(e => e.EvidenceEventIds).HasColumnName("evidence_event_ids");
        analyticsEvent.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc");
        analyticsEvent.Property(e => e.ReceivedAtUtc).HasColumnName("received_at_utc");
        analyticsEvent.HasIndex(e => new { e.ChatId, e.OccurredAtUtc }).HasDatabaseName("ix_analytics_events_chat_date");

        var notification = modelBuilder.Entity<DigestNotificationEntity>();
        notification.ToTable("digest_notifications");
        notification.HasKey(n => n.EventId);
        notification.Property(n => n.EventId).HasColumnName("event_id");
        notification.Property(n => n.ChatId).HasColumnName("chat_id");
        notification.Property(n => n.OccurredAtUtc).HasColumnName("occurred_at_utc");
        notification.Property(n => n.CreatedAtUtc).HasColumnName("created_at_utc");
        notification.Property(n => n.SentAtUtc).HasColumnName("sent_at_utc");
        notification.HasOne<AnalyticsEventEntity>().WithOne().HasForeignKey<DigestNotificationEntity>(n => n.EventId)
            .OnDelete(DeleteBehavior.Cascade);
        notification.HasIndex(n => n.CreatedAtUtc).HasFilter("sent_at_utc IS NULL").HasDatabaseName("ix_digest_notifications_pending");
    }
}
