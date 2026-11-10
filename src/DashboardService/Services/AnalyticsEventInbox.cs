using GroupDashboard.Api.Data;
using GroupDashboard.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Analytics;

namespace GroupDashboard.Api.Services;

public sealed class AnalyticsEventInbox(AppDbContext db,
    TimeProvider timeProvider, ILogger<AnalyticsEventInbox> logger)
{
    public async Task Save(TaskAnalyticsEvent message, CancellationToken ct)
    {
        var kind = message switch
        {
            NewTaskReported => "new_task", BlockerDetected => "blocker", TaskAtRisk => "risk",
            _ => throw new InvalidDataException("Unsupported analytics event.")
        };
        if (message.EventId == Guid.Empty || message.TaskId == Guid.Empty || message.EmployeeId <= 0
            || message.Source != "analytics"
            || string.IsNullOrWhiteSpace(message.CorrelationId) || message.ChatId == 0
            || string.IsNullOrWhiteSpace(message.Title) || message.Title.Length > 2000
            || string.IsNullOrWhiteSpace(message.Description) || message.Description.Length > 10000
            || message.OccurredAt == default || message.EvidenceEventIds is null
            || message.EvidenceEventIds.Length > 1000 || message.EvidenceEventIds.Contains(Guid.Empty))
            throw new InvalidDataException("Invalid analytics event fields.");

        var chatId = message.ChatId;
        var occurredAt = message.OccurredAt.ToUniversalTime();
        var receivedAt = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO analytics_events(event_id,chat_id,task_id,employee_id,kind,title,description,
                evidence_event_ids,occurred_at_utc,received_at_utc)
            VALUES({message.EventId},{chatId},{message.TaskId},{message.EmployeeId},{kind},
                {message.Title},{message.Description},{message.EvidenceEventIds},{occurredAt},{receivedAt})
            ON CONFLICT(event_id) DO NOTHING
            """, ct);
        if (inserted == 1 && chatId is { } id)
        {
            db.DigestNotifications.Add(new DigestNotificationEntity
            {
                EventId = message.EventId, ChatId = id, OccurredAtUtc = occurredAt, CreatedAtUtc = receivedAt
            });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (inserted == 1 && chatId is null)
            logger.LogWarning("Saved legacy analytics event {EventId} without ChatId; no group will receive its data",
                message.EventId);
    }
}
