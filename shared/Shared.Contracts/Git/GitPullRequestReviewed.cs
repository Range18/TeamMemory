namespace Shared.Contracts.Git;

public record GitPullRequestReviewed : GitEventBase
{
    public required int PrNumber { get; init; }
    public required string ReviewerUsername { get; init; }
    public required string ReviewState { get; init; } // "approved", "changes_requested", "commented"
    public string? Body { get; init; }
}
