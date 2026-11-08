namespace Shared.Contracts.Git;

public record GitPullRequestClosed : GitEventBase
{
    public required int PrNumber { get; init; }
}
