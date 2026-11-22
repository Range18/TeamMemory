namespace AnalyticsService.Infrastructure.Persistence;

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
