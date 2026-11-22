using System.Text.Json;
using AnalyticsService.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Analytics;

namespace AnalyticsService.Processing;

public sealed class OutboxPublisher(IDbContextFactory<AnalyticsDbContext> contextFactory, IBus bus, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await contextFactory.CreateDbContextAsync(stoppingToken);
                await using var publisherLock = await DatabaseSessionLock.TryAcquire(db, DatabaseSessionLock.Publisher, stoppingToken);
                if (publisherLock is not null) await PublishPending(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Outbox delivery failed: {ErrorType}", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task PublishPending(AnalyticsDbContext db, CancellationToken ct)
    {
        var pending = await db.OutboxMessages.AsNoTracking().Where(e => e.PublishedAt == null)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId).Take(100).ToListAsync(ct);
        foreach (var item in pending)
        {
            var type = item.EventType switch
            {
                nameof(NewTaskReported) => typeof(NewTaskReported),
                nameof(BlockerDetected) => typeof(BlockerDetected),
                nameof(TaskAtRisk) => typeof(TaskAtRisk),
                _ => throw new InvalidOperationException("Unknown analytics event type.")
            };
            var message = (TaskAnalyticsEvent)JsonSerializer.Deserialize(item.Payload, type)!;
            await bus.Publish(message, type, context => context.MessageId = item.EventId, ct);
            // Crash here may redeliver the same EventId. Subscribers must deduplicate by EventId.
            var publishedAt = DateTimeOffset.UtcNow;
            await db.OutboxMessages.Where(e => e.EventId == item.EventId)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.PublishedAt, publishedAt), ct);
        }
    }
}
