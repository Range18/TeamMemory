using AnalyticsService.Analysis;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Contracts.Analytics;
using Shared.Contracts.Telegram;

namespace AnalyticsService.Infrastructure.Persistence;

public sealed class AnalyticsStore(NpgsqlDataSource dataSource, IOptions<AnalyticsOptions> options)
{
    // One active processor across replicas. A session lock is released by PostgreSQL on disconnect.
    // Inbox ingestion uses other connections and stays independent of LLM latency.
    public const long WorkerLock = 7421983101;
    public const long PublisherLock = 7421983102;

    public async Task Initialize(CancellationToken ct)
    {
        using var stream = typeof(AnalyticsStore).Assembly.GetManifestResourceStream(
            "AnalyticsService.Infrastructure.Persistence.schema.sql")!;
        using var reader = new StreamReader(stream);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, "SELECT pg_advisory_xact_lock(7421983100)", ct);
        await Execute(connection, await reader.ReadToEndAsync(ct), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task Accept(TelegramMessageCreated message, CancellationToken ct)
    {
        if (message.EventId == Guid.Empty || message.ChatId == 0 || message.SenderId is null or <= 0 || message.MessageId <= 0
            || string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > 32000
            || string.IsNullOrWhiteSpace(message.CorrelationId) || message.Source != "telegram"
            || string.IsNullOrWhiteSpace(message.SourceInstance) || message.OccurredAt == default)
            throw new ArgumentException("Invalid Telegram message; stable SenderId and nonempty text are required.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await Execute(connection, """
            INSERT INTO inbox_events(event_id,source_instance,chat_id,message_id,thread_id,
                employee_id,author,text,occurred_at,correlation_id,payload)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11::jsonb)
            ON CONFLICT DO NOTHING
            """, ct, message.EventId, message.SourceInstance, message.ChatId, message.MessageId,
            message.ThreadId ?? 0, message.SenderId.Value, message.SenderUsername, message.Text,
            message.OccurredAt.ToUniversalTime(), message.CorrelationId,
            System.Text.Json.JsonSerializer.Serialize(message));
    }

    public async Task BuildBatches(NpgsqlConnection connection, CancellationToken ct)
    {
        var settings = options.Value;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var groups = new List<(string Source, long Chat, long Thread)>();
        await using (var command = Command(connection, """
            SELECT source_instance,chat_id,thread_id FROM inbox_events WHERE batch_id IS NULL
            GROUP BY source_instance,chat_id,thread_id
            HAVING max(received_at) <= now() - make_interval(secs => $1)
                OR min(received_at) <= now() - make_interval(secs => $2) OR count(*) >= $3
            ORDER BY min(received_at) LIMIT 20
            """, settings.IdleSeconds, settings.MaxWaitSeconds, settings.MaxMessages))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                groups.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        foreach (var group in groups)
        {
            var id = Guid.NewGuid();
            await Execute(connection, """
                INSERT INTO analysis_batches(id,source_instance,chat_id,thread_id) VALUES($1,$2,$3,$4)
                """, ct, id, group.Source, group.Chat, group.Thread);
            await Execute(connection, """
                UPDATE inbox_events SET batch_id=$1 WHERE event_id IN (
                    SELECT event_id FROM inbox_events WHERE batch_id IS NULL
                    AND source_instance=$2 AND chat_id=$3 AND thread_id=$4
                    ORDER BY received_at,event_id LIMIT $5)
                """, ct, id, group.Source, group.Chat, group.Thread, settings.MaxMessages);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<PendingAnalysis?> NextBatch(NpgsqlConnection connection, CancellationToken ct)
    {
        Guid id;
        long chatId;
        string? savedInput, savedResult;
        int attempts;
        await using (var command = Command(connection, """
            SELECT id,input::text,result::text,attempts,chat_id FROM analysis_batches
            WHERE status='ready' AND next_attempt_at <= now() ORDER BY created_at,id LIMIT 1
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            id = reader.GetGuid(0);
            savedInput = reader.IsDBNull(1) ? null : reader.GetString(1);
            savedResult = reader.IsDBNull(2) ? null : reader.GetString(2);
            attempts = reader.GetInt32(3);
            chatId = reader.GetInt64(4);
        }
        if (savedInput is not null)
            return new(AnalysisJson.Read<AnalysisInput>(savedInput) with { ChatId = chatId }, savedResult, attempts);

        var messages = new List<AnalysisMessage>();
        await using (var command = Command(connection, """
            SELECT event_id,message_id,employee_id,author,text,occurred_at FROM inbox_events
            WHERE batch_id=$1 ORDER BY occurred_at,event_id
            """, id))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                messages.Add(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        var tasks = await LoadTasks(connection, ct, chatId, messages.Select(m => m.EmployeeId).Distinct().ToArray());
        // Never silently omit a possible task and then allow the model to invent a duplicate.
        if (tasks.Length > 20)
        {
            await FailBatch(connection, id, "needs_review", "More than 20 candidate tasks.", ct);
            return null;
        }
        var input = new AnalysisInput(id, chatId, messages.ToArray(), tasks);
        await Execute(connection, "UPDATE analysis_batches SET input=$2::jsonb WHERE id=$1", ct, id, AnalysisJson.Write(input));
        return new(input, null, attempts);
    }

    public static async Task<TrackedTask[]> LoadTasks(NpgsqlConnection connection, CancellationToken ct,
        long? chatId = null, long[]? employees = null)
    {
        var result = new List<TrackedTask>();
        await using var command = Command(connection, chatId is null ? "SELECT state::text FROM tasks WHERE chat_id IS NOT NULL" :
            "SELECT state::text FROM tasks WHERE chat_id=$1 AND employee_id=ANY($2)",
            chatId is null ? [] : [chatId.Value, employees!]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(AnalysisJson.Read<TrackedTask>(reader.GetString(0)));
        return result.ToArray();
    }

    public static Task<int> BeginAttempt(NpgsqlConnection connection, Guid id, CancellationToken ct) =>
        Execute(connection, "UPDATE analysis_batches SET attempts=attempts+1 WHERE id=$1", ct, id);

    public static Task<int> SaveResult(NpgsqlConnection connection, Guid id, AnalysisResult result, CancellationToken ct) =>
        Execute(connection, "UPDATE analysis_batches SET result=$2::jsonb WHERE id=$1", ct, id, AnalysisJson.Write(result));

    public static async Task Apply(NpgsqlConnection connection, AnalysisInput input, AppliedAnalysis result, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var check = Command(connection, "SELECT status FROM analysis_batches WHERE id=$1 FOR UPDATE", input.BatchId))
            if ((string?)await check.ExecuteScalarAsync(ct) != "ready") return;
        foreach (var task in result.Tasks)
        {
            var expected = input.Tasks.SingleOrDefault(t => t.Id == task.Id)?.Version;
            int updated;
            if (expected is { } version)
                updated = await Execute(connection, """
                    UPDATE tasks SET state=$2::jsonb,version=$3 WHERE id=$1 AND version=$4
                    """, ct, task.Id, AnalysisJson.Write(task), task.Version, version);
            else
                updated = await Execute(connection, """
                    INSERT INTO tasks(id,chat_id,employee_id,version,state) VALUES($1,$2,$3,$4,$5::jsonb)
                    """, ct, task.Id, task.ChatId, task.EmployeeId, task.Version, AnalysisJson.Write(task));
            if (updated != 1) throw new AnalysisValidationException("Task version changed since analysis.");
        }
        foreach (var change in result.Changes)
            await Execute(connection, """
                INSERT INTO task_changes(id,task_id,batch_id,change_ref,action,payload,effective_at)
                VALUES($1,$2,$3,$4,$5,$6::jsonb,$7)
                """, ct, Guid.NewGuid(), change.Task.Id, input.BatchId, change.Change.ChangeRef,
                change.Change.Action, AnalysisJson.Write(change.Change), change.EffectiveAt.ToUniversalTime());
        foreach (var message in result.Events) await Enqueue(connection, message, ct);
        await Execute(connection, "UPDATE analysis_batches SET status='applied',error=NULL WHERE id=$1", ct, input.BatchId);
        await transaction.CommitAsync(ct);
    }

    public static Task<int> FailBatch(NpgsqlConnection connection, Guid id, string status, string error, CancellationToken ct) =>
        Execute(connection, "UPDATE analysis_batches SET status=$2,error=$3 WHERE id=$1", ct, id, status, error);

    public static Task<int> RetryBatch(NpgsqlConnection connection, Guid id, int attempt, CancellationToken ct) =>
        Execute(connection, """
            UPDATE analysis_batches SET next_attempt_at=now()+make_interval(secs => $2),error='Analysis attempt failed'
            WHERE id=$1
            """, ct, id, Math.Min(300, (int)Math.Pow(2, attempt) * 5));

    public async Task RecalculateRisks(NpgsqlConnection connection, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var task in await LoadTasks(connection, ct))
        {
            var atRisk = TaskChangeApplier.IsAtRisk(task, now, options.Value.RiskMultiplier);
            if (atRisk == task.AtRisk) continue;
            task.AtRisk = atRisk;
            task.Version++;
            await Execute(connection, "UPDATE tasks SET state=$2::jsonb,version=$3 WHERE id=$1", ct,
                task.Id, AnalysisJson.Write(task), task.Version);
            var description = $"Calendar duration exceeds estimate × {options.Value.RiskMultiplier}.";
            await Execute(connection, """
                INSERT INTO task_changes(id,task_id,change_ref,action,payload,effective_at)
                VALUES($1,$2,$3,'set_risk',$4::jsonb,$5)
                """, ct, Guid.NewGuid(), task.Id, $"risk-{task.Version}", AnalysisJson.Write(new { atRisk }), now);
            if (atRisk) await Enqueue(connection, new TaskAtRisk
            {
                Source = "analytics", CorrelationId = task.Id.ToString(),
                ChatId = task.ChatId,
                TaskId = task.Id, Title = task.Title, EmployeeId = task.EmployeeId,
                Description = description, EvidenceEventIds = []
            }, ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static Task<int> Enqueue(NpgsqlConnection connection, TaskAnalyticsEvent message, CancellationToken ct) =>
        Execute(connection, "INSERT INTO outbox_messages(event_id,event_type,payload) VALUES($1,$2,$3::jsonb)", ct,
            message.EventId, message.GetType().Name,
            System.Text.Json.JsonSerializer.Serialize(message, message.GetType()));

    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        return command;
    }

    public static async Task<int> Execute(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed record PendingAnalysis(AnalysisInput Input, string? Result, int Attempts);
