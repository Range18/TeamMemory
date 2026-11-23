using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalyticsService.Analysis;

public static class AnalysisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("Expected a JSON object.");
}

public sealed class AnalyticsOptions : IValidatableObject
{
    public Dictionary<string, string> ChatTeams { get; set; } = [];

    public string GetTeamId(long chatId)
    {
        var id = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ChatTeams.TryGetValue(id, out var teamId) && !string.IsNullOrWhiteSpace(teamId))
            return teamId;

        return id;
    }

    [Range(1, int.MaxValue)]
    public int IdleSeconds { get; set; } = 60;

    [Range(1, int.MaxValue)]
    public int MaxWaitSeconds { get; set; } = 300;

    [Range(1, 100)]
    public int MaxMessages { get; set; } = 100;

    [Range(1, 10)]
    public int PollSeconds { get; set; } = 5;

    [Range(1, int.MaxValue)]
    public int MaxAttempts { get; set; } = 5;

    [Range(0d, 1d)]
    public double MinimumConfidence { get; set; } = .85;

    [Range(double.Epsilon, double.MaxValue)]
    public double RiskMultiplier { get; set; } = 1.5;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxWaitSeconds < IdleSeconds)
            yield return new ValidationResult("MaxWaitSeconds must be at least IdleSeconds.",
                [nameof(MaxWaitSeconds), nameof(IdleSeconds)]);
    }
}

public sealed record AnalysisMessage(Guid EventId, long MessageId, long EmployeeId,
    string Author, string Text, DateTimeOffset OccurredAt);

public sealed class TrackedTask
{
    public Guid Id { get; set; }
    public long ChatId { get; set; }
    public long EmployeeId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "in_progress";
    public string? NextStep { get; set; }
    public double? EstimateDays { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public bool AtRisk { get; set; }
    public int Version { get; set; }
    public List<string> Blockers { get; set; } = [];
}

public sealed record AnalysisInput(Guid BatchId, long ChatId,
    AnalysisMessage[] Messages, TrackedTask[] Tasks);

public sealed record AnalysisResult
{
    public required int SchemaVersion { get; init; }
    public required AnalysisChange[] Changes { get; init; }
    public required string[] Ambiguities { get; init; }
}

public sealed record AnalysisChange
{
    public required string ChangeRef { get; init; }
    public required string Action { get; init; }
    public Guid? TaskId { get; init; }
    public string? NewTaskRef { get; init; }
    public long? EmployeeId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public double? EstimateDays { get; init; }
    public required Guid[] EvidenceEventIds { get; init; }
    public required double Confidence { get; init; }
}

public interface IMessageBatchAnalyzer
{
    Task<AnalysisResult> Analyze(AnalysisInput input, CancellationToken cancellationToken);
}

public sealed class AnalysisValidationException(string message) : Exception(message);
