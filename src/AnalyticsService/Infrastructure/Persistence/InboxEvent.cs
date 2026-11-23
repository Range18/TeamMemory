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
