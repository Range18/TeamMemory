using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using AnalyticsService.Processing;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shared.Contracts.Analytics;
using Shared.Contracts.Telegram;

if (args.Contains("--llm-api-only"))
{
    await LlmApiChecks.Run();
    return;
}

EnvironmentConfigurationChecks.Run();
ValidationChecks.Run();
TaskChangeApplierChecks.Run();
await LlmApiChecks.Run();
if (args.Contains("--analysis-only")) return;

// Only create/drop a uniquely named test database; never modify the supplied database.
var connection = new NpgsqlConnectionStringBuilder(
    Environment.GetEnvironmentVariable("ANALYTICS_CHECKS_CONNECTION")
    ?? "Host=localhost;Port=5433;Username=teammemory;Password=teammemory")
{
    Database = $"analytics_orm_checks_{Guid.NewGuid():N}"
};
var factory = new CheckContextFactory(new DbContextOptionsBuilder<AnalyticsDbContext>()
    .UseNpgsql(connection.ConnectionString).Options);
var settings = new AnalyticsOptions
{
    ChatTeams = new() { ["-100"] = "team-a", ["-200"] = "team-b" },
    MaxMessages = 2
};
var store = new AnalyticsStore(factory, Options.Create(settings));
var ct = CancellationToken.None;

try
{
    await using (var db = factory.CreateDbContext())
        await db.GetService<IRelationalDatabaseCreator>().CreateAsync(ct);
    await store.Initialize(ct);
    await store.Initialize(ct);
    Console.WriteLine("PASS: fresh schema initialization and repeated startup");

    await CheckDeduplication();
    await CheckBatchingAndApplication();
    await CheckVersionConflict();
    await CheckRisk();
    await CheckOutboxDelivery();
    await CheckLocks();
    Console.WriteLine("All PostgreSQL ORM checks passed.");
}
finally
{
    await using var db = factory.CreateDbContext();
    await db.Database.EnsureDeletedAsync(ct);
}

async Task CheckDeduplication()
{
    var message = Message(1);
    await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.Accept(message, ct)));
    await store.Accept(message with { EventId = Guid.NewGuid(), Text = "duplicate delivery" }, ct);
    await using var db = factory.CreateDbContext();
    Assert(await db.InboxEvents.CountAsync(ct) == 1, "Concurrent deliveries must be deduplicated.");
    Assert((await db.InboxEvents.SingleAsync(ct)).Text == message.Text, "Duplicate must not overwrite text.");
    try
    {
        await store.Accept(message with { ChatId = -300 }, ct);
        throw new Exception("Unbound chat was accepted.");
    }
    catch (ArgumentException) { }
    Console.WriteLine("PASS: concurrent deduplication and chat validation");
}

async Task CheckBatchingAndApplication()
{
    await store.Accept(Message(2), ct);
    await store.Accept(Message(3) with { ThreadId = 42 }, ct);
    await store.Accept(Message(4) with { ChatId = -200 }, ct);
    await using var db = factory.CreateDbContext();
    await store.BuildBatches(db, ct);
    Assert(await db.AnalysisBatches.CountAsync(ct) == 1, "Only the group at the size threshold is ready.");
    Assert(await db.InboxEvents.CountAsync(e => e.BatchId == null, ct) == 2, "Other chat/thread must stay pending.");

    var pending = await store.NextBatch(db, ct) ?? throw new Exception("No ready batch.");
    Assert(pending.Input.Messages.Length == 2 && pending.Input.TeamId == "team-a", "Wrong batch input.");
    await AnalyticsStore.BeginAttempt(db, pending.Input.BatchId, ct);
    var change = new AnalysisChange
    {
        ChangeRef = "create-1",
        Action = "create_task",
        NewTaskRef = "new-1",
        EmployeeId = 7,
        Title = "Implement ORM",
        EvidenceEventIds = [pending.Input.Messages[0].EventId],
        Confidence = .99
    };
    var result = new AnalysisResult { SchemaVersion = 1, Changes = [change], Ambiguities = [] };
    await AnalyticsStore.SaveResult(db, pending.Input.BatchId, result, ct);
    await using (var restarted = factory.CreateDbContext())
    {
        var resumed = await store.NextBatch(restarted, ct) ?? throw new Exception("Snapshot lost.");
        Assert(resumed.Attempts == 1 && resumed.Result is not null, "Attempt/result must survive context restart.");
        Assert(AnalysisJson.Write(resumed.Input) == AnalysisJson.Write(pending.Input), "Input snapshot changed.");
    }

    var applied = TaskChangeApplier.Apply(pending.Input, result, settings.MinimumConfidence);
    await AnalyticsStore.Apply(db, pending.Input, applied, ct);
    await AnalyticsStore.Apply(db, pending.Input, applied, ct);
    Assert(await db.Tasks.CountAsync(ct) == 1, "Batch must create one task, even if applied twice.");
    Assert(await db.TaskChanges.CountAsync(ct) == 1, "History must not be duplicated.");
    var published = await db.OutboxMessages.SingleAsync(ct);
    Assert(published.EventType == nameof(NewTaskReported) && published.PublishedAt is null, "Missing pending task event.");
    Assert(await db.AnalysisBatches.AllAsync(b => b.Status == "applied", ct), "Batch not applied.");
    db.ChangeTracker.Clear();

    // Exercise both time-based triggers without waiting in real time.
    await db.InboxEvents.Where(e => e.BatchId == null && e.ThreadId == 42)
        .ExecuteUpdateAsync(update => update.SetProperty(e => e.ReceivedAt, DateTimeOffset.UtcNow.AddSeconds(-61)), ct);
    await store.BuildBatches(db, ct);
    Assert(await db.AnalysisBatches.CountAsync(ct) == 2, "Idle group not built.");
    await store.Accept(Message(5) with { ChatId = -200 }, ct);
    settings.MaxMessages = 100;
    await db.InboxEvents.Where(e => e.ChatId == -200 && e.MessageId == 4)
        .ExecuteUpdateAsync(update => update.SetProperty(e => e.ReceivedAt, DateTimeOffset.UtcNow.AddSeconds(-301)), ct);
    await store.BuildBatches(db, ct);
    Assert(await db.AnalysisBatches.CountAsync(ct) == 3, "Maximum wait must trigger a group despite recent messages.");
    Console.WriteLine("PASS: size/idle/wait triggers, isolation, snapshots, task/history/outbox and idempotent application");
}

async Task CheckVersionConflict()
{
    await using var db = factory.CreateDbContext();
    var pending = await store.NextBatch(db, ct) ?? throw new Exception("No pending batch.");
    var task = pending.Input.Tasks.Single();
    var change = new AnalysisChange
    {
        ChangeRef = "progress",
        Action = "record_progress",
        TaskId = task.Id,
        Description = "Updated",
        Confidence = .99,
        EvidenceEventIds = [pending.Input.Messages[0].EventId]
    };
    var result = TaskChangeApplier.Apply(pending.Input,
        new AnalysisResult { SchemaVersion = 1, Changes = [change], Ambiguities = [] }, settings.MinimumConfidence);
    await using (var concurrent = factory.CreateDbContext())
        await concurrent.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.Version, t => t.Version + 1), ct);
    try
    {
        await AnalyticsStore.Apply(db, pending.Input, result, ct);
        throw new Exception("Stale task version was accepted.");
    }
    catch (AnalysisValidationException) { }
    db.ChangeTracker.Clear();
    Assert(await db.TaskChanges.CountAsync(ct) == 1, "Failed transaction wrote history.");
    Assert(await db.OutboxMessages.CountAsync(ct) == 1, "Failed transaction wrote outbox.");
    Assert((await db.AnalysisBatches.SingleAsync(b => b.Id == pending.Input.BatchId, ct)).Status == "ready",
        "Failed transaction changed batch status.");
    db.ChangeTracker.Clear();
    await AnalyticsStore.RetryBatch(db, pending.Input.BatchId, 1, ct);
    Assert((await db.AnalysisBatches.AsNoTracking().SingleAsync(b => b.Id == pending.Input.BatchId, ct)).NextAttemptAt
        > DateTimeOffset.UtcNow, "Retry must be delayed.");
    await AnalyticsStore.FailBatch(db, pending.Input.BatchId, "needs_review", "version conflict", ct);
    Console.WriteLine("PASS: optimistic concurrency, atomic rollback and retry/review updates");
}

async Task CheckRisk()
{
    await using var db = factory.CreateDbContext();
    var entity = await db.Tasks.SingleAsync(ct);
    var task = AnalysisJson.Read<TrackedTask>(entity.State);
    task.Version = entity.Version;
    task.StartedAt = DateTimeOffset.UtcNow.AddDays(-4);
    task.EstimateDays = 2;
    entity.State = AnalysisJson.Write(task);
    await db.SaveChangesAsync(ct);
    db.ChangeTracker.Clear();
    await store.RecalculateRisks(db, ct);
    await store.RecalculateRisks(db, ct);
    Assert(AnalysisJson.Read<TrackedTask>((await db.Tasks.AsNoTracking().SingleAsync(ct)).State).AtRisk,
        "Overdue task must be at risk.");
    Assert(await db.OutboxMessages.CountAsync(e => e.EventType == nameof(TaskAtRisk), ct) == 1,
        "Risk event must be emitted once per transition.");
    Assert(await db.TaskChanges.CountAsync(e => e.Action == "set_risk", ct) == 1, "Risk history duplicated.");
    Console.WriteLine("PASS: risk recalculation, JSON round trip and event deduplication");
}

async Task CheckLocks()
{
    await using var first = factory.CreateDbContext();
    await using var second = factory.CreateDbContext();
    var held = await DatabaseSessionLock.TryAcquire(first, DatabaseSessionLock.Worker, ct)
        ?? throw new Exception("Cannot acquire worker lock.");
    await using (held)
    {
        await using var blocked = await DatabaseSessionLock.TryAcquire(second, DatabaseSessionLock.Worker, ct);
        Assert(blocked is null, "Second worker must not acquire the same lock.");
        await using var publisher = await DatabaseSessionLock.TryAcquire(second, DatabaseSessionLock.Publisher, ct);
        Assert(publisher is not null, "Publisher lock must be independent.");
    }
    await using var reacquired = await DatabaseSessionLock.TryAcquire(second, DatabaseSessionLock.Worker, ct);
    Assert(reacquired is not null, "Lock must be released after disposal.");
    Console.WriteLine("PASS: cross-connection worker exclusion, independent publisher and lock release");
}

async Task CheckOutboxDelivery()
{
    var received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
    var bus = Bus.Factory.CreateUsingInMemory(configuration => configuration.ReceiveEndpoint("orm-checks", endpoint =>
        endpoint.Handler<NewTaskReported>(context =>
        {
            Assert(context.MessageId == context.Message.EventId, "Transport MessageId must preserve EventId.");
            received.TrySetResult(context.Message.EventId);
            return Task.CompletedTask;
        })));
    await bus.StartAsync(ct);
    using var publisher = new OutboxPublisher(factory, bus, NullLogger<OutboxPublisher>.Instance);
    try
    {
        await publisher.StartAsync(ct);
        var eventId = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var db = factory.CreateDbContext();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await db.OutboxMessages.AnyAsync(e => e.PublishedAt == null, ct))
        {
            Assert(DateTimeOffset.UtcNow < deadline, "Published outbox messages were not marked delivered.");
            await Task.Delay(100, ct);
        }
        Assert(await db.OutboxMessages.AnyAsync(e => e.EventId == eventId && e.PublishedAt != null, ct),
            "Delivered event was not found in outbox.");
    }
    finally
    {
        await publisher.StopAsync(ct);
        await bus.StopAsync(ct);
    }
    Console.WriteLine("PASS: outbox publication, stable MessageId and publication markers using in-memory transport");
}

static TelegramMessageCreated Message(long id) => new()
{
    Source = "telegram",
    CorrelationId = Guid.NewGuid().ToString(),
    ChatId = -100,
    MessageId = id,
    SenderId = 7,
    SenderUsername = "developer",
    Text = $"Work update {id}"
};

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class CheckContextFactory(DbContextOptions<AnalyticsDbContext> options) : IDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext() => new(options);
}
