using System.Text.Json;
using AnalyticsService.Infrastructure.Persistence;
using MassTransit;
using Npgsql;
using Shared.Contracts.Analytics;

namespace AnalyticsService.Processing;

public sealed class OutboxPublisher(NpgsqlDataSource dataSource, IBus bus, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                await using var publisherLock = await DatabaseSessionLock.TryAcquire(connection, AnalyticsStore.PublisherLock, stoppingToken);
                if (publisherLock is not null) await PublishPending(connection, stoppingToken);
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

    private async Task PublishPending(NpgsqlConnection connection, CancellationToken ct)
    {
        var pending = new List<(Guid Id, string Type, string Payload)>();
        await using (var command = AnalyticsStore.Command(connection, """
            SELECT event_id,event_type,payload::text FROM outbox_messages
            WHERE published_at IS NULL ORDER BY created_at,event_id LIMIT 100
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) pending.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        foreach (var item in pending)
        {
            var type = item.Type switch
            {
                nameof(NewTaskReported) => typeof(NewTaskReported),
                nameof(BlockerDetected) => typeof(BlockerDetected),
                nameof(TaskAtRisk) => typeof(TaskAtRisk),
                _ => throw new InvalidOperationException("Unknown analytics event type.")
            };
            var message = (TaskAnalyticsEvent)JsonSerializer.Deserialize(item.Payload, type)!;
            await bus.Publish(message, type, context => context.MessageId = item.Id, ct);
            // Crash here may redeliver the same EventId. Subscribers must deduplicate by EventId.
            await AnalyticsStore.Execute(connection,
                "UPDATE outbox_messages SET published_at=now() WHERE event_id=$1", ct, item.Id);
        }
    }
}
