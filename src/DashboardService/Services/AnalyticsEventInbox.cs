using GroupDashboard.Api.Data;
using GroupDashboard.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Contracts.Analytics;

namespace GroupDashboard.Api.Services;

public sealed class AnalyticsEventInbox(AppDbContext db, IOptions<DigestOptions> options,
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
            || message.Source != "analytics" || string.IsNullOrWhiteSpace(message.TeamId) || message.TeamId.Length > 200
            || string.IsNullOrWhiteSpace(message.CorrelationId) || message.ChatId == 0
            || string.IsNullOrWhiteSpace(message.Title) || message.Title.Length > 2000
            || string.IsNullOrWhiteSpace(message.Description) || message.Description.Length > 10000
            || message.OccurredAt == default || message.EvidenceEventIds is null
            || message.EvidenceEventIds.Length > 1000 || message.EvidenceEventIds.Contains(Guid.Empty))
            throw new InvalidDataException("Invalid analytics event fields.");

        var chatId = message.ChatId;
        if (chatId is null && options.Value.LegacyTeamChats.TryGetValue(message.TeamId, out var mappedChat))
            chatId = mappedChat;
        var occurredAt = message.OccurredAt.ToUniversalTime();
        var receivedAt = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO analytics_events(event_id,chat_id,team_id,task_id,employee_id,kind,title,description,
                evidence_event_ids,occurred_at_utc,received_at_utc)
            VALUES({message.EventId},{chatId},{message.TeamId},{message.TaskId},{message.EmployeeId},{kind},
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
            logger.LogWarning("Saved analytics event {EventId} without group routing; configure Digest:LegacyTeamChats for old producers",
                message.EventId);
    }
}
