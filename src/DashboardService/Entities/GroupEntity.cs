namespace GroupDashboard.Api.Entities;

public sealed class GroupEntity
{
    // Telegram chat_id может быть отрицательным для групп и супергрупп.
    public long ChatId { get; set; }
    public required string Title { get; set; }
    public string? Username { get; set; }
    public int? MemberCount { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<GroupManagerEntity> Managers { get; set; } = new List<GroupManagerEntity>();
}
