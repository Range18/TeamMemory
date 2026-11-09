namespace GroupDashboard.Api.Contracts;

public sealed record DigestEntryDto(Guid EventId, Guid TaskId, string EmployeeId,
    string Title, string Description, DateTimeOffset OccurredAtUtc, Guid[] EvidenceEventIds);

public sealed record DigestDto(string ChatId, string GroupTitle, string Date, string TimeZone,
    string Text, DigestEntryDto[] NewTasks, DigestEntryDto[] Blockers, DigestEntryDto[] Risks,
    int EventCount, DateTimeOffset GeneratedAtUtc);

// Invalidation after database commit. The page fetches its authorized, selected date again.
public sealed record DigestUpdatedMessage(string ChatId, string Date);
