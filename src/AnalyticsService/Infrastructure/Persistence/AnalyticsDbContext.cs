using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AnalyticsService.Infrastructure.Persistence;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<InboxEvent> InboxEvents => Set<InboxEvent>();
    public DbSet<AnalysisBatch> AnalysisBatches => Set<AnalysisBatch>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskChange> TaskChanges => Set<TaskChange>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<InboxEvent>();
        inbox.ToTable("inbox_events");
        inbox.HasKey(e => e.EventId);
        inbox.HasIndex(e => new { e.SourceInstance, e.ChatId, e.MessageId }).IsUnique();
        inbox.HasIndex(e => e.ReceivedAt).HasDatabaseName("ix_inbox_pending");
        inbox.HasIndex(e => e.BatchId).HasDatabaseName("ix_inbox_batch");
        inbox.HasOne<AnalysisBatch>().WithMany().HasForeignKey(e => e.BatchId)
            .OnDelete(DeleteBehavior.NoAction);
        inbox.Property(e => e.Payload).HasColumnType("jsonb");

        var batches = modelBuilder.Entity<AnalysisBatch>();
        batches.ToTable("analysis_batches");
        batches.HasKey(e => e.Id);
        batches.Property(e => e.Status).IsConcurrencyToken();
        batches.Property(e => e.Input).HasColumnType("jsonb");
        batches.Property(e => e.Result).HasColumnType("jsonb");
        batches.HasIndex(e => e.NextAttemptAt).HasDatabaseName("ix_batches_ready");

        var tasks = modelBuilder.Entity<TaskEntity>();
        tasks.ToTable("tasks");
        tasks.HasKey(e => e.Id);
        tasks.Property(e => e.Version).IsConcurrencyToken();
        tasks.Property(e => e.State).HasColumnType("jsonb");
        tasks.HasIndex(e => new { e.TeamId, e.EmployeeId }).HasDatabaseName("ix_tasks_team_employee");

        var changes = modelBuilder.Entity<TaskChange>();
        changes.ToTable("task_changes");
        changes.HasKey(e => e.Id);
        changes.Property(e => e.Payload).HasColumnType("jsonb");
        changes.HasIndex(e => new { e.BatchId, e.ChangeRef }).IsUnique();
        changes.HasOne<TaskEntity>().WithMany().HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.NoAction);
        changes.HasOne<AnalysisBatch>().WithMany().HasForeignKey(e => e.BatchId)
            .OnDelete(DeleteBehavior.NoAction);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("outbox_messages");
        outbox.HasKey(e => e.EventId);
        outbox.Property(e => e.Payload).HasColumnType("jsonb");
        outbox.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_outbox_pending");

        // Keep the column names used by the existing PostgreSQL schema.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
