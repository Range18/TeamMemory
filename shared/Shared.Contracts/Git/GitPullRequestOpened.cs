namespace Shared.Contracts.Git;

public record GitPullRequestOpened : GitEventBase
{
    public required int PrNumber { get; init; }
    public required string Title { get; init; }
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
}
