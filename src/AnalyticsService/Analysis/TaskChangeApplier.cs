using Shared.Contracts.Analytics;

namespace AnalyticsService.Analysis;

public sealed record AppliedChange(TrackedTask Task, AnalysisChange Change, DateTimeOffset EffectiveAt);
public sealed record AppliedAnalysis(TrackedTask[] Tasks, AppliedChange[] Changes, TaskAnalyticsEvent[] Events);

public static class TaskChangeApplier
{
    public static AppliedAnalysis Apply(AnalysisInput input, AnalysisResult result, double minimumConfidence)
    {
        if (result.SchemaVersion != 1 || result.Changes is null || result.Ambiguities is null || result.Ambiguities.Length != 0)
            throw new AnalysisValidationException("Unsupported schema or ambiguous analysis.");

        // Work on copies: invalid operations cannot leave partially modified state.
        var tasks = input.Tasks.ToDictionary(t => t.Id, t => AnalysisJson.Read<TrackedTask>(AnalysisJson.Write(t)));
        var newTasks = new Dictionary<string, TrackedTask>();
        var refs = new HashSet<string>();
        var touched = new HashSet<Guid>();
        var changes = new List<AppliedChange>();
        var events = new List<TaskAnalyticsEvent>();
        foreach (var change in result.Changes)
        {
            if (change is null || string.IsNullOrWhiteSpace(change.ChangeRef) || !refs.Add(change.ChangeRef)
                || !double.IsFinite(change.Confidence) || change.Confidence < minimumConfidence || change.Confidence > 1
                || change.EvidenceEventIds is null || change.EvidenceEventIds.Length == 0)
                throw new AnalysisValidationException("Invalid change reference, confidence or evidence.");
            var evidence = change.EvidenceEventIds.Select(id => input.Messages.SingleOrDefault(m => m.EventId == id)
                ?? throw new AnalysisValidationException("Evidence is outside the batch.")).ToArray();
            var effectiveAt = evidence.Max(m => m.OccurredAt);
            TrackedTask task;
            if (change.Action == "create_task")
            {
                if (change.TaskId is not null || string.IsNullOrWhiteSpace(change.NewTaskRef)
                    || newTasks.ContainsKey(change.NewTaskRef) || change.EmployeeId is not { } employeeId
                    || !evidence.Any(m => m.EmployeeId == employeeId))
                    throw new AnalysisValidationException("Invalid new task or employee.");
                task = new TrackedTask
                {
                    Id = Guid.NewGuid(), TeamId = input.TeamId, EmployeeId = employeeId,
                    Title = RequiredText(change.Title), StartedAt = effectiveAt, LastActivityAt = effectiveAt
                };
                newTasks.Add(change.NewTaskRef, task);
                tasks.Add(task.Id, task);
                events.Add(CreateEvent(input, task, change, task.Title, false));
            }
            else
            {
                if ((change.TaskId is null) == (change.NewTaskRef is null))
                    throw new AnalysisValidationException("Specify exactly one task reference.");
                task = change.TaskId is { } id
                    ? tasks.GetValueOrDefault(id) ?? throw new AnalysisValidationException("Unknown task.")
                    : newTasks.GetValueOrDefault(change.NewTaskRef!) ?? throw new AnalysisValidationException("Unknown new task reference.");
                var original = input.Tasks.SingleOrDefault(t => t.Id == task.Id);
                if (task.TeamId != input.TeamId || (original is not null && effectiveAt < original.LastActivityAt))
                    throw new AnalysisValidationException("Task is outside team or evidence is older than current state.");
                switch (change.Action)
                {
                    case "record_progress":
                        RequiredText(change.Description);
                        break;
                    case "add_blocker":
                        if (task.Status == "done")
                            throw new AnalysisValidationException("Cannot block a completed task.");
                        var blocker = RequiredText(change.Description);
                        if (!task.Blockers.Contains(blocker, StringComparer.OrdinalIgnoreCase))
                        {
                            task.Blockers.Add(blocker);
                            events.Add(CreateEvent(input, task, change, blocker, true));
                        }
                        task.Status = "blocked";
                        break;
                    case "resolve_blocker":
                        if (!task.Blockers.Remove(RequiredText(change.Description)))
                            throw new AnalysisValidationException("Unknown open blocker.");
                        if (task.Blockers.Count == 0) task.Status = "in_progress";
                        break;
                    case "set_next_step":
                        task.NextStep = RequiredText(change.Description);
                        break;
                    case "set_estimate":
                        if (change.EstimateDays is not { } days || !double.IsFinite(days) || days <= 0 || days > 3650)
                            throw new AnalysisValidationException("Invalid calendar-day estimate.");
                        task.EstimateDays = days;
                        break;
                    case "set_status":
                        if (change.Status is not ("in_progress" or "done") || task.Blockers.Count != 0)
                            throw new AnalysisValidationException("Invalid status transition.");
                        task.Status = change.Status;
                        if (task.Status == "done") task.AtRisk = false;
                        break;
                    default:
                        throw new AnalysisValidationException("Unknown action.");
                }
            }
            task.LastActivityAt = effectiveAt > task.LastActivityAt ? effectiveAt : task.LastActivityAt;
            touched.Add(task.Id);
            changes.Add(new(task, change, effectiveAt));
        }
        foreach (var id in touched) tasks[id].Version++;
        return new(touched.Select(id => tasks[id]).ToArray(), changes.ToArray(), events.ToArray());
    }

    public static bool IsAtRisk(TrackedTask task, DateTimeOffset now, double multiplier) =>
        task.Status != "done" && task.EstimateDays is { } days && (now - task.StartedAt).TotalDays > days * multiplier;

    private static string RequiredText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 2000
        ? text : throw new AnalysisValidationException("Expected nonempty text up to 2000 characters.");

    private static TaskAnalyticsEvent CreateEvent(AnalysisInput input, TrackedTask task,
        AnalysisChange change, string description, bool blocker) => blocker
        ? new BlockerDetected
        {
            Source = "analytics", CorrelationId = input.BatchId.ToString(), TeamId = input.TeamId,
            TaskId = task.Id, Title = task.Title, EmployeeId = task.EmployeeId,
            Description = description, EvidenceEventIds = change.EvidenceEventIds
        }
        : new NewTaskReported
        {
            Source = "analytics", CorrelationId = input.BatchId.ToString(), TeamId = input.TeamId,
            TaskId = task.Id, Title = task.Title, EmployeeId = task.EmployeeId,
            Description = description, EvidenceEventIds = change.EvidenceEventIds
        };
}
