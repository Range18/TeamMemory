namespace GroupDashboard.Api.Entities;

public sealed class GroupManagerEntity
{
    public long ChatId { get; set; }
    public long ManagerTelegramId { get; set; }
    public DateTimeOffset ConnectedAtUtc { get; set; }

    public GroupEntity Group { get; set; } = null!;
}
