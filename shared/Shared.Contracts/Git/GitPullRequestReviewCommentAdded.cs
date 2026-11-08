namespace Shared.Contracts.Git;

public record GitPullRequestReviewCommentAdded : GitEventBase
{
    public required int PrNumber { get; init; }
    public required string ReviewerUsername { get; init; }
    public required string CommentText { get; init; }
    public required string FilePath { get; init; }
}
