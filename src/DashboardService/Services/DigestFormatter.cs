using System.Globalization;
using System.Text.RegularExpressions;
using GroupDashboard.Api.Contracts;
using GroupDashboard.Api.Entities;

namespace GroupDashboard.Api.Services;

public static partial class DigestFormatter
{
    public static DigestDto Format(long chatId, string title, DateOnly date, DigestOptions options,
        IEnumerable<AnalyticsEventEntity> events, DateTimeOffset now)
    {
        var items = events.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.EventId).ToArray();
        DigestEntryDto[] Section(string kind) => items.Where(e => e.Kind == kind).Select(e => new DigestEntryDto(
            e.EventId, e.TaskId, e.EmployeeId.ToString(CultureInfo.InvariantCulture), e.Title, e.Description,
            e.OccurredAtUtc, e.EvidenceEventIds)).ToArray();
        var tasks = Section("new_task");
        var blockers = Section("blocker");
        var risks = Section("risk");
        var values = new Dictionary<string, string>
        {
            ["group_title"] = OneLine(title), ["date"] = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["new_tasks"] = Lines(tasks), ["blockers"] = Lines(blockers), ["risks"] = Lines(risks)
        };
        // One pass: placeholders in message text are data and cannot expand the template.
        var text = Placeholder().Replace(options.Template, match => values[match.Groups[1].Value]);
        return new(chatId.ToString(CultureInfo.InvariantCulture), title,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), options.TimeZone,
            text, tasks, blockers, risks, items.Length, now);
    }

    public static bool IsValidTemplate(string template) => !string.IsNullOrWhiteSpace(template)
        && template.Length <= 10000 && new[] { "group_title", "date", "new_tasks", "blockers", "risks" }
            .All(key => template.Contains("{{" + key + "}}", StringComparison.Ordinal));

    public static (DateTimeOffset From, DateTimeOffset To) UtcPeriod(DateOnly date, TimeZoneInfo zone) =>
        (TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone),
         TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));

    private static string Lines(DigestEntryDto[] items) => items.Length == 0 ? "— Нет новых сообщений."
        : string.Join("\n", items.Select(e => $"• {OneLine(e.Title)} — сотрудник {e.EmployeeId}"
            + (e.Description == e.Title ? "." : $": {OneLine(e.Description)}")));

    private static string OneLine(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"\{\{(group_title|date|new_tasks|blockers|risks)\}\}")]
    private static partial Regex Placeholder();
}
