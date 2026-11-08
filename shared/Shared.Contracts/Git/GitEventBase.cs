namespace Shared.Contracts.Git;

public abstract record GitEventBase : IntegrationEventBase
{
    public required string RepositoryName { get; init; }
    public required string ActorUsername { get; init; }
}
