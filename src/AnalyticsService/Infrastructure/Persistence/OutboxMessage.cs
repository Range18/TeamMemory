namespace AnalyticsService.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}
