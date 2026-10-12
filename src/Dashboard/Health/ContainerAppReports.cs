using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// What the pipeline's hourly health reports found for one container app of an environment: an entry of
/// <c>health.containerApps</c> in <c>delivery.json</c>, which the system's workflow reads from the line the runbook
/// "Health report" writes for the app. The page probes the app only while it shows it (<see cref="ContainerAppProbe"/>);
/// this is the history under that probe. Hourly checks, not monitoring.
/// </summary>
/// <param name="Name">The deployable the app runs, as <c>system.json</c> names it.</param>
/// <param name="Last">The newest report that asked it; null when none did in seven days.</param>
/// <param name="Last24Hours">
/// The reports of the last 24 hours that asked it, and those of them that found it healthy; null when the file does
/// not count them (a report of that time whose log was not read).
/// </param>
/// <param name="Last7Days">The same for the last seven days.</param>
public sealed record ContainerAppReports(string Name, HourlyCheck? Last, HealthWindow? Last24Hours, HealthWindow? Last7Days)
{
    /// <summary>The entries of <c>containerApps</c> of an environment's <c>health</c>; null without such an array.</summary>
    internal static IReadOnlyList<ContainerAppReports>? ReadAll(JsonElement? health)
    {
        if (health is not { ValueKind: JsonValueKind.Object } section
            || !section.TryGetProperty("containerApps", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return [.. array.EnumerateArray().Select(Read).OfType<ContainerAppReports>()];
    }

    /// <summary>An entry; null without a name.</summary>
    private static ContainerAppReports? Read(JsonElement element) =>
        JsonRead.Text(element, "name") is { } name
            ? new ContainerAppReports(
                name,
                HourlyCheck.Read(JsonRead.Section(element, "last")),
                HealthWindow.Read(JsonRead.Section(element, "last24Hours")),
                HealthWindow.Read(JsonRead.Section(element, "last7Days")))
            : null;
}

/// <summary>What one hourly health report found for a container app, and when.</summary>
/// <param name="Result"><see cref="Healthy"/>, <see cref="Unhealthy"/> or <see cref="Unreachable"/>.</param>
/// <param name="At">When that report ended.</param>
public sealed record HourlyCheck(string Result, DateTimeOffset At)
{
    public const string Healthy = "healthy";
    public const string Unhealthy = "unhealthy";
    public const string Unreachable = "unreachable";

    /// <summary>The result; null unless it is one of the three words and has its time.</summary>
    internal static HourlyCheck? Read(JsonElement? section)
    {
        var result = JsonRead.Text(section, "result")?.ToLowerInvariant();
        return result is Healthy or Unhealthy or Unreachable && JsonRead.Time(section, "at") is { } at ? new HourlyCheck(result, at) : null;
    }
}

/// <summary>
/// The hourly health report on a container app's box, in neutral words: <c>hourly: healthy, 40 min ago</c>, then
/// <c>24 h: 23 of 24 · 7 d: 160 of 168</c>. Never a state of the box: the page's own probe is, and a report says how
/// the app answered once, up to two hours ago.
/// </summary>
public static class HourlyCheckText
{
    /// <summary>
    /// How old the newest result may be and still be said: the runbook runs hourly, and the delivery facts are
    /// published hourly, so a result is up to two hours old when the box shows it. Older, the box says
    /// <see cref="NoRecentCheck"/>: a report that stopped is no news about the app.
    /// </summary>
    public static readonly TimeSpan Recent = TimeSpan.FromHours(2);

    /// <summary>The words of a box without a result of the last two hours.</summary>
    public const string NoRecentCheck = "no recent hourly check";

    /// <summary>The newest result when it is at most <see cref="Recent"/> old; null otherwise.</summary>
    public static HourlyCheck? RecentResult(ContainerAppReports? reports, DateTimeOffset now) =>
        reports?.Last is { } last && now - last.At <= Recent ? last : null;

    /// <summary><c>hourly: healthy, 40 min ago</c>; <see cref="NoRecentCheck"/> without a result of the last two hours.</summary>
    public static string Result(ContainerAppReports? reports, DateTimeOffset now) =>
        RecentResult(reports, now) is { } last ? $"hourly: {last.Result}, {TimeText.Ago(last.At, now)}" : NoRecentCheck;

    /// <summary>
    /// <c>24 h: 23 of 24 · 7 d: 160 of 168</c>: of the reports that asked the app, those that found it healthy. A time
    /// the file does not count is left out, and one without a report says "none"; null when neither is counted.
    /// </summary>
    public static string? Counts(ContainerAppReports? reports)
    {
        var parts = new List<string>();
        if (reports?.Last24Hours is { } day)
        {
            parts.Add($"24 h: {Count(day)}");
        }

        if (reports?.Last7Days is { } week)
        {
            parts.Add($"7 d: {Count(week)}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The tooltip: what the report is, what it last found and when, and what it counted.</summary>
    public static string Title(ContainerAppReports? reports, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var words = new List<string>
        {
            "Once an hour the pipeline's runbook \"Health report\" in Octopus Deploy asks the app whether it answers, also while no page is open: the history, under the page's own probe.",
        };
        if (reports?.Last is { } last)
        {
            var when = $"{TimeText.DateAndTime(last.At, zone)} ({TimeText.Ago(last.At, now)})";
            words.Add(RecentResult(reports, now) is null
                ? $"The last report that asked it ended {when} and found it {last.Result}: none in the last two hours, so the box says {NoRecentCheck}."
                : $"The last report that asked it ended {when} and found it {last.Result}.");
        }
        else
        {
            words.Add(reports is null ? "The delivery facts name no report of it yet." : "No report of the last 7 days asked it.");
        }

        if (reports?.Last24Hours is { } day)
        {
            words.Add(string.Create(CultureInfo.InvariantCulture, $"Healthy in {day.Healthy} of the {day.Reports} reports of 24 hours that asked it."));
        }

        if (reports?.Last7Days is { } week)
        {
            words.Add(string.Create(CultureInfo.InvariantCulture, $"Healthy in {week.Healthy} of the {week.Reports} reports of 7 days that asked it."));
        }

        words.Add(AvailabilityText.Note);
        return string.Join(' ', words);
    }

    private static string Count(HealthWindow window) => window.Reports == 0 ? "none" : AvailabilityText.Count(window);
}
