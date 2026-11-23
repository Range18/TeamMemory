namespace AnalyticsService.Infrastructure.Persistence;

public sealed class TaskEntity
{
    public Guid Id { get; set; }
    public string TeamId { get; set; } = "";
    public long EmployeeId { get; set; }
    public int Version { get; set; }
    public string State { get; set; } = "";
}
