using GroupDashboard.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupDashboard.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GroupEntity> Groups => Set<GroupEntity>();
    public DbSet<GroupManagerEntity> GroupManagers => Set<GroupManagerEntity>();

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
    }
}
