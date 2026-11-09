using System.Globalization;
using GroupDashboard.Api.Contracts;
using GroupDashboard.Api.Data;
using GroupDashboard.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GroupDashboard.Api.Services;

public sealed class DigestNotificationWorker(IServiceScopeFactory scopes, IHubContext<GroupsHub> hub,
    IOptions<DigestOptions> options, TimeProvider timeProvider, ILogger<DigestNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Dispatch(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Could not send pending digest notifications"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.NotificationPollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task Dispatch(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Competing workers skip claimed rows. A failed send rolls back, keeping the notification pending.
        var pending = await db.DigestNotifications.FromSqlRaw("""
            SELECT * FROM digest_notifications WHERE sent_at_utc IS NULL
            ORDER BY created_at_utc,event_id LIMIT 50 FOR UPDATE SKIP LOCKED
            """).ToArrayAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        foreach (var group in pending.GroupBy(n => n.ChatId))
        {
            var managers = await db.GroupManagers.AsNoTracking().Where(m => m.ChatId == group.Key)
                .Select(m => m.ManagerTelegramId).ToArrayAsync(ct);
            foreach (var date in group.Select(n => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(n.OccurredAtUtc, zone).DateTime)).Distinct())
            {
                var notification = new DigestUpdatedMessage(group.Key.ToString(CultureInfo.InvariantCulture),
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                foreach (var manager in managers)
                    // SignalR groups contain only authenticated active sessions of this manager.
                    await hub.Clients.Group(GroupsHub.ManagerGroup(manager)).SendAsync("DigestUpdated", notification, ct);
            }
            foreach (var item in group) item.SentAtUtc = timeProvider.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
