namespace Shared.Contracts;

public abstract record IntegrationEventBase
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public required string Source { get; init; } // "telegram" или "github"
    public required string CorrelationId { get; init; }
}
