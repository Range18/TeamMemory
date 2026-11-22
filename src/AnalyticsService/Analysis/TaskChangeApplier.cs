using Shared.Contracts.Analytics;

namespace AnalyticsService.Analysis;

public static class TaskChangeApplier
{
    public static AppliedAnalysis Apply(AnalysisInput input, AnalysisResult result, double minimumConfidence)
    {
        ValidateResult(result);

        // Change copies so a rejected batch cannot modify the input snapshot.
        var tasks = input.Tasks.ToDictionary(task => task.Id, CopyTask);
        var newTasks = new Dictionary<string, TrackedTask>();
        var changeReferences = new HashSet<string>();
        var changedTaskIds = new HashSet<Guid>();
        var appliedChanges = new List<AppliedChange>();
        var events = new List<TaskAnalyticsEvent>();

        foreach (var change in result.Changes)
        {
            ValidateChange(change, changeReferences, minimumConfidence);
            var evidence = FindEvidence(input, change);
            var effectiveAt = evidence.Max(message => message.OccurredAt);
            TrackedTask task;

            if (change.Action == "create_task")
            {
                task = CreateTask(input.TeamId, change, evidence, effectiveAt, newTasks);
                newTasks.Add(change.NewTaskRef!, task);
                tasks.Add(task.Id, task);
                events.Add(CreateTaskReportedEvent(input, task, change));
            }
            else
            {
                task = FindTask(change, tasks, newTasks);
                ValidateTaskChange(input, task, effectiveAt);
                ApplyTaskChange(input, task, change, events);
            }

            if (effectiveAt > task.LastActivityAt)
                task.LastActivityAt = effectiveAt;

            changedTaskIds.Add(task.Id);
            appliedChanges.Add(new AppliedChange(task, change, effectiveAt));
        }

        var changedTasks = changedTaskIds.Select(id => tasks[id]).ToArray();
        foreach (var task in changedTasks)
            task.Version++;

        return new AppliedAnalysis(changedTasks, appliedChanges.ToArray(), events.ToArray());
    }

    public static bool IsAtRisk(TrackedTask task, DateTimeOffset now, double multiplier) =>
        task.Status != "done" && task.EstimateDays is { } days && (now - task.StartedAt).TotalDays > days * multiplier;

    private static void ValidateResult(AnalysisResult result)
    {
        if (result.SchemaVersion != 1)
            throw new AnalysisValidationException("Unsupported analysis schema version.");
        if (result.Changes is null || result.Ambiguities is null)
            throw new AnalysisValidationException("Changes and ambiguities are required.");
        if (result.Ambiguities.Length > 0)
            throw new AnalysisValidationException("Ambiguous analysis requires review.");
    }

    private static void ValidateChange(AnalysisChange change, HashSet<string> references, double minimumConfidence)
    {
        if (change is null)
            throw new AnalysisValidationException("An analysis change must not be null.");
        if (string.IsNullOrWhiteSpace(change.ChangeRef))
            throw new AnalysisValidationException("A change reference is required.");
        if (!references.Add(change.ChangeRef))
            throw new AnalysisValidationException("Change references must be unique within the batch.");
        if (!double.IsFinite(change.Confidence) || change.Confidence < minimumConfidence || change.Confidence > 1)
            throw new AnalysisValidationException("Change confidence is outside the accepted range.");
        if (change.EvidenceEventIds is null || change.EvidenceEventIds.Length == 0)
            throw new AnalysisValidationException("At least one evidence message is required.");
    }

    private static AnalysisMessage[] FindEvidence(AnalysisInput input, AnalysisChange change) =>
        change.EvidenceEventIds.Select(eventId => input.Messages.SingleOrDefault(message => message.EventId == eventId)
            ?? throw new AnalysisValidationException("Evidence is outside the batch.")).ToArray();

    private static TrackedTask CreateTask(string teamId, AnalysisChange change, AnalysisMessage[] evidence,
        DateTimeOffset effectiveAt, Dictionary<string, TrackedTask> newTasks)
    {
        if (change.TaskId is not null)
            throw new AnalysisValidationException("A new task must not specify task_id.");
        if (string.IsNullOrWhiteSpace(change.NewTaskRef))
            throw new AnalysisValidationException("A new task reference is required.");
        if (newTasks.ContainsKey(change.NewTaskRef))
            throw new AnalysisValidationException("A new task reference must be unique within the batch.");
        if (change.EmployeeId is not { } employeeId)
            throw new AnalysisValidationException("A new task must specify an employee.");
        if (!evidence.Any(message => message.EmployeeId == employeeId))
            throw new AnalysisValidationException("The employee must be an author of an evidence message.");

        return new TrackedTask
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            EmployeeId = employeeId,
            Title = RequireText(change.Title),
            StartedAt = effectiveAt,
            LastActivityAt = effectiveAt
        };
    }

    private static TrackedTask FindTask(AnalysisChange change, Dictionary<Guid, TrackedTask> tasks,
        Dictionary<string, TrackedTask> newTasks)
    {
        var hasTaskId = change.TaskId is not null;
        var hasNewTaskReference = change.NewTaskRef is not null;
        if (hasTaskId == hasNewTaskReference)
            throw new AnalysisValidationException("Specify exactly one task reference.");

        if (change.TaskId is { } taskId)
            return tasks.GetValueOrDefault(taskId)
                ?? throw new AnalysisValidationException("Unknown task.");

        return newTasks.GetValueOrDefault(change.NewTaskRef!)
            ?? throw new AnalysisValidationException("Unknown new task reference.");
    }

    private static void ValidateTaskChange(AnalysisInput input, TrackedTask task, DateTimeOffset effectiveAt)
    {
        if (task.TeamId != input.TeamId)
            throw new AnalysisValidationException("Task is outside the team.");

        // Compare against the original snapshot, not earlier changes in this batch.
        var originalTask = input.Tasks.SingleOrDefault(original => original.Id == task.Id);
        if (originalTask is not null && effectiveAt < originalTask.LastActivityAt)
            throw new AnalysisValidationException("Evidence is older than the current task state.");
    }

    private static void ApplyTaskChange(AnalysisInput input, TrackedTask task, AnalysisChange change,
        List<TaskAnalyticsEvent> events)
    {
        switch (change.Action)
        {
            case "record_progress":
                RequireText(change.Description);
                break;
            case "add_blocker":
                AddBlocker(input, task, change, events);
                break;
            case "resolve_blocker":
                ResolveBlocker(task, change.Description);
                break;
            case "set_next_step":
                task.NextStep = RequireText(change.Description);
                break;
            case "set_estimate":
                SetEstimate(task, change.EstimateDays);
                break;
            case "set_status":
                SetStatus(task, change.Status);
                break;
            default:
                throw new AnalysisValidationException("Unknown action.");
        }
    }

    private static void AddBlocker(AnalysisInput input, TrackedTask task, AnalysisChange change,
        List<TaskAnalyticsEvent> events)
    {
        if (task.Status == "done")
            throw new AnalysisValidationException("Cannot block a completed task.");

        var blocker = RequireText(change.Description);
        if (!task.Blockers.Contains(blocker, StringComparer.OrdinalIgnoreCase))
        {
            task.Blockers.Add(blocker);
            events.Add(CreateBlockerDetectedEvent(input, task, change, blocker));
        }
        task.Status = "blocked";
    }

    private static void ResolveBlocker(TrackedTask task, string? description)
    {
        if (!task.Blockers.Remove(RequireText(description)))
            throw new AnalysisValidationException("Unknown open blocker.");
        if (task.Blockers.Count == 0)
            task.Status = "in_progress";
    }

    private static void SetEstimate(TrackedTask task, double? estimateDays)
    {
        if (estimateDays is not { } days)
            throw new AnalysisValidationException("A calendar-day estimate is required.");
        if (!double.IsFinite(days) || days <= 0 || days > 3650)
            throw new AnalysisValidationException("Calendar-day estimate must be finite, positive and at most 3650.");

        task.EstimateDays = days;
    }

    private static void SetStatus(TrackedTask task, string? status)
    {
        if (status is not ("in_progress" or "done"))
            throw new AnalysisValidationException("Status must be in_progress or done.");
        if (task.Blockers.Count > 0)
            throw new AnalysisValidationException("Cannot set status while the task has open blockers.");

        task.Status = status;
        if (status == "done")
            task.AtRisk = false;
    }

    private static string RequireText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new AnalysisValidationException("Nonempty text is required.");
        if (text.Length > 2000)
            throw new AnalysisValidationException("Text must not exceed 2000 characters.");

        return text;
    }

    private static TrackedTask CopyTask(TrackedTask task) =>
        AnalysisJson.Read<TrackedTask>(AnalysisJson.Write(task));

    private static NewTaskReported CreateTaskReportedEvent(AnalysisInput input, TrackedTask task, AnalysisChange change) =>
        new()
        {
            Source = "analytics",
            CorrelationId = input.BatchId.ToString(),
            TeamId = input.TeamId,
            TaskId = task.Id,
            Title = task.Title,
            EmployeeId = task.EmployeeId,
            Description = task.Title,
            EvidenceEventIds = change.EvidenceEventIds
        };

    private static BlockerDetected CreateBlockerDetectedEvent(AnalysisInput input, TrackedTask task,
        AnalysisChange change, string description) => new()
        {
            Source = "analytics",
            CorrelationId = input.BatchId.ToString(),
            TeamId = input.TeamId,
            TaskId = task.Id,
            Title = task.Title,
            EmployeeId = task.EmployeeId,
            Description = description,
            EvidenceEventIds = change.EvidenceEventIds
        };
}
