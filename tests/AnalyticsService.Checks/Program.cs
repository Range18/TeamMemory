using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Contracts.Analytics;
using Shared.Contracts.Telegram;
using AnalyticsService.Consumers;
using AnalyticsService.Processing;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;

var now = DateTimeOffset.UtcNow;
var task = new TrackedTask
{
    Id = Guid.NewGuid(), TeamId = "team", EmployeeId = 42, Title = "Интеграция API клиента",
    StartedAt = now.AddDays(-4), LastActivityAt = now.AddDays(-4), EstimateDays = 2, Version = 1
};
var messages = new[]
{
    new AnalysisMessage(Guid.NewGuid(), 1, 42, "ivan", "Авторизацию закончил", now.AddSeconds(-40)),
    new AnalysisMessage(Guid.NewGuid(), 2, 42, "ivan", "Жду доступ к production API", now.AddSeconds(-20)),
    new AnalysisMessage(Guid.NewGuid(), 3, 42, "ivan", "После доступа проверю интеграцию", now)
};
var input = new AnalysisInput(Guid.NewGuid(), "team", messages, [task]);
AnalysisChange Change(string action, int message, string? description = null) => new()
{
    ChangeRef = Guid.NewGuid().ToString(), Action = action, TaskId = task.Id,
    Description = description, EvidenceEventIds = [messages[message].EventId], Confidence = .95
};
AnalysisResult Result(params AnalysisChange[] changes) => new() { SchemaVersion = 1, Changes = changes, Ambiguities = [] };
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}
void Reject(AnalysisResult result, string name)
{
    try { TaskChangeApplier.Apply(input, result, .85); }
    catch (AnalysisValidationException) { Check(true, name); return; }
    throw new Exception("FAILED to reject: " + name);
}

var analysis = Result(Change("record_progress", 0, "Реализована авторизация"),
    Change("add_blocker", 1, "Ожидает доступ к production API от клиента"),
    Change("set_next_step", 2, "Проверить интеграцию после получения доступа"));
var applied = TaskChangeApplier.Apply(input, analysis, .85);
Check(applied.Tasks.Single().Status == "blocked" && applied.Tasks[0].Blockers.Count == 1
    && applied.Tasks[0].NextStep is not null && applied.Changes.Length == 3
    && applied.Events.Single() is BlockerDetected, "progress, blocker and next step from three messages");
Check(task.Status == "in_progress" && task.Blockers.Count == 0, "input snapshot remains immutable");
Check(TaskChangeApplier.IsAtRisk(task, now, 1.5)
    && !TaskChangeApplier.IsAtRisk(task, task.StartedAt.AddDays(3), 1.5), "calendar-day risk threshold is strict");
Check(TaskChangeApplier.Apply(input, Result(), .85).Tasks.Length == 0, "ordinary conversation has no side effects");
Reject(Result(Change("add_blocker", 1, "blocker") with { EvidenceEventIds = [Guid.NewGuid()] }), "foreign evidence");
Reject(Result(Change("add_blocker", 1, "blocker") with { TaskId = Guid.NewGuid() }), "unknown task");
Reject(Result(Change("add_blocker", 1, "blocker") with { Confidence = .5 }), "low confidence");
Reject(Result(Change("set_estimate", 0) with { EstimateDays = -1 }), "invalid estimate");
Reject(Result(Change("add_blocker", 1, "blocker"), Change("set_status", 2) with { Status = "done" }), "cannot complete blocked task");
Check(task.Blockers.Count == 0, "rejected analysis does not partially mutate task");
Reject(analysis with { Ambiguities = ["unclear task"] }, "ambiguity");
var oldInput = input with { Tasks = [new TrackedTask
{
    Id = task.Id, TeamId = task.TeamId, EmployeeId = 42, LastActivityAt = now.AddDays(1)
}] };
try { TaskChangeApplier.Apply(oldInput, analysis, .85); throw new Exception("Accepted stale evidence"); }
catch (AnalysisValidationException) { Check(true, "old evidence cannot overwrite current state"); }
var newTask = new AnalysisChange
{
    ChangeRef = "new", Action = "create_task", NewTaskRef = "task-1", EmployeeId = 42,
    Title = "Новая задача", EvidenceEventIds = [messages[0].EventId], Confidence = .95
};
var created = TaskChangeApplier.Apply(input, Result(newTask,
    Change("add_blocker", 1, "Нет доступа") with { TaskId = null, NewTaskRef = "task-1" }), .85);
Check(created.Events.OfType<NewTaskReported>().Count() == 1 && created.Events.OfType<BlockerDetected>().Count() == 1
    && created.Tasks.Single().Status == "blocked", "new task reference and outgoing events");
try { AnalysisJson.Read<AnalysisResult>("{\"schema_version\":1,\"changes\":[],\"ambiguities\":[],\"sql\":\"DROP TABLE tasks\"}");
    throw new Exception("Accepted unknown JSON field"); }
catch (System.Text.Json.JsonException) { Check(true, "strict JSON parsing rejects unknown fields"); }

if (!args.Contains("--integration")) return;

// Use only the isolated database supplied by tests/docker-compose.yml.
await using var dataSource = NpgsqlDataSource.Create(
    "Host=localhost;Port=5433;Database=analytics_checks;Username=teammemory;Password=teammemory");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = timeout.Token;
var store = new AnalyticsStore(dataSource, Options.Create(new AnalyticsOptions
{
    ChatTeams = new() { ["100"] = "team" }, IdleSeconds = 1, MaxWaitSeconds = 1
}));
await store.Initialize(ct);
await using var connection = await dataSource.OpenConnectionAsync(ct);
var delivery = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
var expectedEventId = Guid.Empty;
var run = Guid.NewGuid().ToString("N");
var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
{
    cfg.Host("localhost", 5673, "/", h => { h.Username("teammemory"); h.Password("teammemory"); });
    cfg.ReceiveEndpoint("checks-inbox-" + run, e =>
    {
        e.Durable = false; e.AutoDelete = true;
        e.Consumer(() => new TelegramMessageCreatedConsumer(store));
    });
    cfg.ReceiveEndpoint("checks-dashboard-" + run, e =>
    {
        e.Durable = false; e.AutoDelete = true;
        e.Handler<NewTaskReported>(context =>
        {
            if (context.Message.EventId == expectedEventId) delivery.TrySetResult(context.Message.EventId);
            return Task.CompletedTask;
        });
    });
});
await bus.StartAsync(ct);
try
{
var chat = new TelegramMessageCreated
{
    Source = "telegram", CorrelationId = "checks", SourceInstance = Guid.NewGuid().ToString(),
    ChatId = 100, MessageId = 1, SenderId = 42, SenderUsername = "ivan", Text = "Работаю над API"
};
await bus.Publish(chat, ct);
await bus.Publish(chat, ct);
await bus.Publish(chat with { EventId = Guid.NewGuid() }, ct);
for (var i = 0; i < 100; i++)
{
    await using var count = AnalyticsStore.Command(connection,
        "SELECT count(*) FROM inbox_events WHERE source_instance=$1", chat.SourceInstance);
    if ((long)(await count.ExecuteScalarAsync(ct))! == 1) break;
    await Task.Delay(100, ct);
}
await using (var count = AnalyticsStore.Command(connection,
    "SELECT count(*) FROM inbox_events WHERE source_instance=$1", chat.SourceInstance))
    Check((long)(await count.ExecuteScalarAsync(ct))! == 1, "persistent EventId and source-message deduplication");
await Task.Delay(1200, ct);
await store.BuildBatches(connection, ct);
// Other runs may have ready batches; select this run's specific batch.
Guid batchId;
await using (var find = AnalyticsStore.Command(connection,
    "SELECT batch_id FROM inbox_events WHERE event_id=$1", chat.EventId))
    batchId = (Guid)(await find.ExecuteScalarAsync(ct))!;
var dbInput = new AnalysisInput(batchId, "team",
    [new(chat.EventId, chat.MessageId, 42, "ivan", chat.Text, chat.OccurredAt)], []);
var dbResult = Result(newTask with { EvidenceEventIds = [chat.EventId] });
await AnalyticsStore.SaveResult(connection, batchId, dbResult, ct);
var dbApplied = TaskChangeApplier.Apply(dbInput, dbResult, .85);
expectedEventId = dbApplied.Events.Single().EventId;
await AnalyticsStore.Apply(connection, dbInput, dbApplied, ct);
await AnalyticsStore.Apply(connection, dbInput, dbApplied, ct);
await using (var count = AnalyticsStore.Command(connection,
    "SELECT count(*) FROM task_changes WHERE batch_id=$1", batchId))
    Check((long)(await count.ExecuteScalarAsync(ct))! == 1, "atomic and idempotent application");
await using (var count = AnalyticsStore.Command(connection,
    "SELECT count(*) FROM outbox_messages WHERE event_id=$1", dbApplied.Events.Single().EventId))
    Check((long)(await count.ExecuteScalarAsync(ct))! == 1, "task and outgoing event saved together");
using var publisher = new OutboxPublisher(dataSource, bus, NullLogger<OutboxPublisher>.Instance);
await publisher.StartAsync(ct);
try
{
    Check(await delivery.Task.WaitAsync(ct) == dbApplied.Events.Single().EventId,
        "outbox publishes typed event to Dashboard subscription over RabbitMQ");
}
finally { await publisher.StopAsync(CancellationToken.None); }
Console.WriteLine("PostgreSQL and RabbitMQ integration checks completed.");
}
finally { await bus.StopAsync(CancellationToken.None); }
