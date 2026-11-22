namespace AnalyticsService.Infrastructure.Persistence;

public sealed class InboxEvent
{
    public Guid EventId { get; set; }
    public string SourceInstance { get; set; } = "";
    public long ChatId { get; set; }
    public long MessageId { get; set; }
    public long ThreadId { get; set; }
    public string TeamId { get; set; } = "";
    public long EmployeeId { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CorrelationId { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid? BatchId { get; set; }
}

public sealed class AnalysisBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TeamId { get; set; } = "";
    public string SourceInstance { get; set; } = "";
    public long ChatId { get; set; }
    public long ThreadId { get; set; }
    public string Status { get; set; } = "ready";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Input { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}

public sealed class TaskEntity
{
    public Guid Id { get; set; }
    public string TeamId { get; set; } = "";
    public long EmployeeId { get; set; }
    public int Version { get; set; }
    public string State { get; set; } = "";
}

public sealed class TaskChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public Guid? BatchId { get; set; }
    public string ChangeRef { get; set; } = "";
    public string Action { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutboxMessage
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}
