using System.Text.Json;
using AnalyticsService.Analysis;
using AnalyticsService.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Contracts.Analytics;
using Shared.Contracts.Telegram;

namespace AnalyticsService.Infrastructure.Persistence;

public sealed class AnalyticsStore(IDbContextFactory<AnalyticsDbContext> contextFactory, IOptions<AnalyticsOptions> options)
{
    public async Task Initialize(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var initializerLock = await DatabaseSessionLock.Acquire(db, DatabaseSessionLock.Initializer, ct);
        // Existing tables keep their data and JSON format; a fresh database is created from the EF model.
        await db.Database.EnsureCreatedAsync(ct);
    }

    public async Task Accept(TelegramMessageCreated message, CancellationToken ct)
    {
        var chatId = message.ChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        options.Value.ChatTeams.TryGetValue(chatId, out var teamId);
        var dto = TelegramMessageDto.FromEvent(message, teamId);
        dto.Validate();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.InboxEvents.Add(new InboxEvent
        {
            EventId = dto.EventId,
            SourceInstance = dto.SourceInstance,
            ChatId = dto.ChatId,
            MessageId = dto.MessageId,
            ThreadId = dto.ThreadId,
            TeamId = dto.TeamId!,
            EmployeeId = dto.SenderId!.Value,
            Author = dto.SenderUsername,
            Text = dto.Text,
            OccurredAt = dto.OccurredAt,
            CorrelationId = dto.CorrelationId,
            Payload = JsonSerializer.Serialize(message)
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "inbox_events" })
        {
            // Both EventId and the source/chat/message identity are unique, including concurrent deliveries.
        }
    }

    public async Task BuildBatches(AnalyticsDbContext db, CancellationToken ct)
    {
        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;
        var idleBefore = now.AddSeconds(-settings.IdleSeconds);
        var waitingBefore = now.AddSeconds(-settings.MaxWaitSeconds);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var groups = await db.InboxEvents.Where(e => e.BatchId == null)
            .GroupBy(e => new { e.TeamId, e.SourceInstance, e.ChatId, e.ThreadId })
            .Where(g => g.Max(e => e.ReceivedAt) <= idleBefore
                || g.Min(e => e.ReceivedAt) <= waitingBefore || g.Count() >= settings.MaxMessages)
            .OrderBy(g => g.Min(e => e.ReceivedAt))
            .Select(g => g.Key).Take(20).ToListAsync(ct);

        foreach (var group in groups)
        {
            var batch = new AnalysisBatch
            {
                TeamId = group.TeamId,
                SourceInstance = group.SourceInstance,
                ChatId = group.ChatId,
                ThreadId = group.ThreadId
            };
            var messages = await db.InboxEvents.Where(e => e.BatchId == null && e.TeamId == group.TeamId
                    && e.SourceInstance == group.SourceInstance && e.ChatId == group.ChatId && e.ThreadId == group.ThreadId)
                .OrderBy(e => e.ReceivedAt).ThenBy(e => e.EventId).Take(settings.MaxMessages).ToListAsync(ct);
            db.AnalysisBatches.Add(batch);
            foreach (var message in messages) message.BatchId = batch.Id;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<PendingAnalysis?> NextBatch(AnalyticsDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = await db.AnalysisBatches.AsNoTracking()
            .Where(b => b.Status == "ready" && b.NextAttemptAt <= now)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).FirstOrDefaultAsync(ct);
        if (batch is null) return null;
        if (batch.Input is not null)
            return new(AnalysisJson.Read<AnalysisInput>(batch.Input), batch.Result, batch.Attempts);

        var messages = await db.InboxEvents.AsNoTracking().Where(e => e.BatchId == batch.Id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.EventId)
            .Select(e => new AnalysisMessage(e.EventId, e.MessageId, e.EmployeeId, e.Author, e.Text, e.OccurredAt))
            .ToArrayAsync(ct);
        var employees = messages.Select(m => m.EmployeeId).Distinct().ToArray();
        var states = await db.Tasks.Where(t => t.TeamId == batch.TeamId && employees.Contains(t.EmployeeId))
            .Select(t => t.State).ToArrayAsync(ct);
        if (states.Length > 20)
        {
            await FailBatch(db, batch.Id, "needs_review", "More than 20 candidate tasks.", ct);
            return null;
        }
        var input = new AnalysisInput(batch.Id, batch.TeamId, messages,
            states.Select(AnalysisJson.Read<TrackedTask>).ToArray());
        var json = AnalysisJson.Write(input);
        await db.AnalysisBatches.Where(b => b.Id == batch.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(b => b.Input, json), ct);
        return new(input, null, batch.Attempts);
    }

    public static Task<int> BeginAttempt(AnalyticsDbContext db, Guid id, CancellationToken ct) =>
        db.AnalysisBatches.Where(b => b.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(b => b.Attempts, b => b.Attempts + 1), ct);

    public static Task<int> SaveResult(AnalyticsDbContext db, Guid id, AnalysisResult result, CancellationToken ct)
    {
        var json = AnalysisJson.Write(result);
        return db.AnalysisBatches.Where(b => b.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(b => b.Result, json), ct);
    }

    public static async Task Apply(AnalyticsDbContext db, AnalysisInput input, AppliedAnalysis result, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.AnalysisBatches.SingleAsync(b => b.Id == input.BatchId, ct);
        if (batch.Status != "ready") return;

        foreach (var task in result.Tasks)
        {
            var entity = new TaskEntity
            {
                Id = task.Id,
                TeamId = task.TeamId,
                EmployeeId = task.EmployeeId,
                Version = task.Version,
                State = AnalysisJson.Write(task)
            };
            if (input.Tasks.SingleOrDefault(t => t.Id == task.Id) is { } original)
            {
                var entry = db.Tasks.Attach(entity);
                entry.Property(t => t.State).IsModified = true;
                entry.Property(t => t.Version).IsModified = true;
                entry.Property(t => t.Version).OriginalValue = original.Version;
            }
            else db.Tasks.Add(entity);
        }
        db.TaskChanges.AddRange(result.Changes.Select(change => new TaskChange
        {
            TaskId = change.Task.Id,
            BatchId = input.BatchId,
            ChangeRef = change.Change.ChangeRef,
            Action = change.Change.Action,
            Payload = AnalysisJson.Write(change.Change),
            EffectiveAt = change.EffectiveAt.ToUniversalTime()
        }));
        foreach (var message in result.Events) Enqueue(db, message);
        batch.Status = "applied";
        batch.Error = null;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new AnalysisValidationException("Task version or batch status changed since analysis.");
        }
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public static Task<int> FailBatch(AnalyticsDbContext db, Guid id, string status, string error, CancellationToken ct) =>
        db.AnalysisBatches.Where(b => b.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(b => b.Status, status).SetProperty(b => b.Error, error), ct);

    public static Task<int> RetryBatch(AnalyticsDbContext db, Guid id, int attempt, CancellationToken ct)
    {
        var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, (int)Math.Pow(2, attempt) * 5));
        return db.AnalysisBatches.Where(b => b.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(b => b.NextAttemptAt, nextAttempt).SetProperty(b => b.Error, "Analysis attempt failed"), ct);
    }

    public async Task RecalculateRisks(AnalyticsDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var entity in await db.Tasks.ToListAsync(ct))
        {
            var task = AnalysisJson.Read<TrackedTask>(entity.State);
            var atRisk = TaskChangeApplier.IsAtRisk(task, now, options.Value.RiskMultiplier);
            if (atRisk == task.AtRisk) continue;
            task.AtRisk = atRisk;
            task.Version++;
            entity.State = AnalysisJson.Write(task);
            entity.Version = task.Version;
            db.TaskChanges.Add(new TaskChange
            {
                TaskId = task.Id,
                ChangeRef = $"risk-{task.Version}",
                Action = "set_risk",
                Payload = AnalysisJson.Write(new { atRisk }),
                EffectiveAt = now
            });
            if (atRisk) Enqueue(db, new TaskAtRisk
            {
                Source = "analytics",
                CorrelationId = task.Id.ToString(),
                TeamId = task.TeamId,
                TaskId = task.Id,
                Title = task.Title,
                EmployeeId = task.EmployeeId,
                Description = $"Calendar duration exceeds estimate × {options.Value.RiskMultiplier}.",
                EvidenceEventIds = []
            });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static void Enqueue(AnalyticsDbContext db, TaskAnalyticsEvent message) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventId = message.EventId,
            EventType = message.GetType().Name,
            Payload = JsonSerializer.Serialize(message, message.GetType())
        });
}

public sealed record PendingAnalysis(AnalysisInput Input, string? Result, int Attempts);
