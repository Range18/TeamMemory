namespace GroupDashboard.Api.Contracts;

public sealed record GroupDto(
    string ChatId,
    string Title,
    string? Username,
    int? MemberCount,
    string Status,
    DateTimeOffset UpdatedAtUtc);
