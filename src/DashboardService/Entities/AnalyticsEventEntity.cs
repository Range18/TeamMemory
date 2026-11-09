namespace GroupDashboard.Api.Entities;

public sealed class AnalyticsEventEntity
{
    public Guid EventId { get; set; }
    public long? ChatId { get; set; }
    public required string TeamId { get; set; }
    public Guid TaskId { get; set; }
    public long EmployeeId { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public Guid[] EvidenceEventIds { get; set; } = [];
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public sealed class DigestNotificationEntity
{
    public Guid EventId { get; set; }
    public long ChatId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
}
