namespace Shared.Contracts.Git;

public record GitPullRequestMerged : GitEventBase
{
    public required int PrNumber { get; init; }
}
