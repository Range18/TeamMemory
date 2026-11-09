namespace Shared.Contracts.Analytics;

public abstract record TaskAnalyticsEvent : IntegrationEventBase
{
    public required string TeamId { get; init; }
    public required Guid TaskId { get; init; }
    public required string Title { get; init; }
    public required long EmployeeId { get; init; }
    public required string Description { get; init; }
    public required Guid[] EvidenceEventIds { get; init; }
}

public sealed record NewTaskReported : TaskAnalyticsEvent;
public sealed record BlockerDetected : TaskAnalyticsEvent;
public sealed record TaskAtRisk : TaskAnalyticsEvent;
