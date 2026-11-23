namespace AnalyticsService.Infrastructure.Persistence;

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
