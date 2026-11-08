namespace Shared.Contracts.Git;

public record GitPushCommitted : GitEventBase
{
    public required string Branch { get; init; }
    public required IReadOnlyList<CommitInfo> Commits { get; init; }
}

public record CommitInfo
{
    public required string Sha { get; init; }
    public required string Message { get; init; }
    public required string AuthorName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
