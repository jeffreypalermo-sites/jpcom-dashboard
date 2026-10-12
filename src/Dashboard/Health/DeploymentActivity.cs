using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>What a deployment that is stopped for a person waits for (<c>waitsFor</c> of an entry of the file).</summary>
/// <param name="Kind"><c>sign-off</c>, or <c>guided failure</c> for the question Octopus Deploy asks after a step failed; another word in the file's own.</param>
/// <param name="Title">Octopus Deploy's title of the question; null when the file gives none.</param>
/// <param name="Since">When it was asked; null when the file's time does not parse.</param>
/// <param name="Responsible">
/// Who is responsible: the person who took it, otherwise the teams Octopus Deploy names. Null when the file names
/// nobody (the system could not read the names).
/// </param>
public sealed record DeploymentWait(string Kind, string? Title, DateTimeOffset? Since, string? Responsible)
{
    public const string SignOff = "sign-off";
    public const string GuidedFailure = "guided failure";

    public bool IsSignOff => string.Equals(Kind, SignOff, StringComparison.OrdinalIgnoreCase);

    public bool IsGuidedFailure => string.Equals(Kind, GuidedFailure, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a title that says no more than the kind does (<c>Sign-off</c> for a sign-off).</summary>
    public bool TitleIsTheKind => Title is null || string.Equals(Title.Replace(" ", "-", StringComparison.Ordinal), Kind.Replace(" ", "-", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    /// <summary>Null for an entry without the field, or without a kind.</summary>
    internal static DeploymentWait? Read(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } && JsonRead.Text(element, "kind") is { } kind
            ? new DeploymentWait(kind, JsonRead.Text(element, "title"), JsonRead.Time(element, "since"), JsonRead.Text(element, "responsible"))
            : null;
}

/// <summary>The last deployment of a project to an environment that ended (an entry of <c>recent</c>).</summary>
/// <param name="Result"><see cref="DeploymentState.Succeeded"/>, <see cref="DeploymentState.Failed"/> or <see cref="DeploymentState.Canceled"/>; <see cref="DeploymentState.Unknown"/> for another word.</param>
/// <param name="ResultText">The result in the file's own word.</param>
/// <param name="StartedBy">Who started it; null when the file does not say.</param>
/// <param name="Url">The task in Octopus Deploy; null when the file gives no address.</param>
public sealed record RecentDeployment(
    string Project,
    string Environment,
    string Release,
    DeploymentState Result,
    string ResultText,
    DateTimeOffset Finished,
    string? StartedBy,
    Uri? Url)
{
    /// <summary>An entry without a project, an environment, a release, a result or a time it ended at is left out.</summary>
    internal static RecentDeployment? Read(JsonElement element) =>
        JsonRead.Text(element, "project") is { } project && JsonRead.Text(element, "environment") is { } environment
            && JsonRead.Text(element, "release") is { } release && JsonRead.Text(element, "result") is { } result
            && JsonRead.Time(element, "finished") is { } finished
            ? new RecentDeployment(
                project,
                environment,
                release,
                Deployment.Of(result) is var state and (DeploymentState.Succeeded or DeploymentState.Failed or DeploymentState.Canceled) ? state : DeploymentState.Unknown,
                result,
                finished,
                JsonRead.Text(element, "startedBy"),
                JsonRead.Address(element, "url"))
            : null;
}

/// <summary>A deployment freeze of the Octopus Deploy instance that covers the system (an entry of <c>freezes</c>).</summary>
/// <param name="From">When it begins; null when the file's time does not parse.</param>
/// <param name="To">When it ends.</param>
/// <param name="Active">Whether it was in force when the system read it: used only where <paramref name="From"/> is not known.</param>
/// <param name="Environments">The environments it covers, by name.</param>
/// <param name="Projects">The Octopus projects of the system it covers, by name.</param>
public sealed record DeploymentFreeze(
    string Name,
    DateTimeOffset? From,
    DateTimeOffset To,
    bool Active,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> Projects)
{
    /// <summary>
    /// How far ahead a freeze is named before it begins, by the browser's clock: what the system writes
    /// (<c>write-deployments.ps1 -FreezeHours</c>), so a file that is read late names no more than a fresh one.
    /// </summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromHours(72);

    /// <summary>In force at <paramref name="now"/>, by the browser's clock: begun and not ended.</summary>
    public bool InForceAt(DateTimeOffset now) => now < To && (From is { } from ? from <= now : Active);

    /// <summary>Not begun at <paramref name="now"/>, and beginning within <see cref="Ahead"/>.</summary>
    public bool ComesAt(DateTimeOffset now) => From is { } from && from > now && from - now <= Ahead && now < To;

    /// <summary>True when it covers the environment, and the project when one is asked for.</summary>
    public bool Covers(string environment, string? project = null) =>
        Environments.Contains(environment, StringComparer.OrdinalIgnoreCase)
        && (project is null || Projects.Contains(project, StringComparer.OrdinalIgnoreCase));

    /// <summary>An entry without a name or a time it ends at is left out.</summary>
    internal static DeploymentFreeze? Read(JsonElement element) =>
        JsonRead.Text(element, "name") is { } name && JsonRead.Time(element, "to") is { } to
            ? new DeploymentFreeze(name, JsonRead.Time(element, "from"), to, JsonRead.Flag(element, "active") ?? false, Names(element, "environments"), Names(element, "projects"))
            : null;

    private static List<string> Names(JsonElement element, string name) =>
        [.. JsonRead.Items(element, name).Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(text => text.Length > 0)];
}

/// <summary>
/// One line of what is happening, or just happened, to a deployable in an environment or to the environment: on a
/// node of the runtime view, and under an environment's name.
/// </summary>
/// <param name="Kind">
/// What the line is about, as the word its class and the runtime payload carry: <see cref="ActivityKind"/>. The most
/// urgent kinds come first.
/// </param>
/// <param name="Text">The line: short on a node, in full under an environment's name.</param>
/// <param name="Title">The same in a full sentence, with the times: the tooltip.</param>
/// <param name="Url">The task in Octopus Deploy; null for a freeze, and when the file gives no address.</param>
public sealed record ActivityLine(string Kind, string Text, string Title, Uri? Url = null);

/// <summary>The kinds of an <see cref="ActivityLine"/>, in the order of their urgency.</summary>
public static class ActivityKind
{
    /// <summary>A deployment is stopped for a person: somebody has to act.</summary>
    public const string Waiting = "waiting";

    /// <summary>A deployment is running.</summary>
    public const string Deploying = "deploying";

    /// <summary>A deployment waits its turn.</summary>
    public const string Queued = "queued";

    /// <summary>A deployment freeze is in force.</summary>
    public const string Frozen = "frozen";

    /// <summary>A deployment freeze begins soon.</summary>
    public const string Freeze = "freeze";

    /// <summary>The last deployment that ended succeeded.</summary>
    public const string Deployed = "deployed";

    /// <summary>The last deployment that ended failed.</summary>
    public const string Failed = "failed";

    /// <summary>The last deployment that ended was canceled, or ended in a way the page does not know.</summary>
    public const string Canceled = "canceled";

    public static IReadOnlyList<string> All { get; } = [Waiting, Deploying, Queued, Frozen, Freeze, Deployed, Failed, Canceled];
}

/// <summary>
/// What is happening and what just happened, in words, from the system's deployments file
/// (<see cref="DeploymentsReport"/>): what waits for a person and who is responsible, what is deploying or queued, a
/// deployment freeze in force or coming, and the last deployment that ended. Minutes old, as the file is.
/// </summary>
public static class DeploymentActivity
{
    /// <summary>How many lines a node of the runtime view carries, and how many follow the marks under an environment's name.</summary>
    public const int MostLines = 2;

    /// <summary>How many characters a line on a node holds: the tile is 232 px wide and the line has an icon.</summary>
    public const int NodeLength = 36;

    /// <summary>
    /// The lines of one deployable in one environment, the most urgent first and <see cref="MostLines"/> at most: a
    /// deployment that waits for a person, one that is executing, one that is queued, a freeze in force, a freeze
    /// that comes, and the last deployment that ended. They do not name the deployable or the environment: the node
    /// they stand on is both. Empty where the file has nothing about it, and for a file from before the lists
    /// (<see cref="DeploymentsReport.SaysActivity"/>): such a file is drawn as it was, with the dot alone.
    /// </summary>
    /// <param name="slug">The system's slug: the deployable's Octopus project is <c>&lt;slug&gt;-&lt;deployable&gt;</c>.</param>
    public static IReadOnlyList<ActivityLine> OfNode(DeploymentsReport report, string environment, string slug, string deployable, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        if (!report.SaysActivity)
        {
            return [];
        }

        var project = $"{slug}-{deployable}";
        bool Own(string name, string where) =>
            string.Equals(name, project, StringComparison.OrdinalIgnoreCase) && string.Equals(where, environment, StringComparison.OrdinalIgnoreCase);

        var lines = new List<ActivityLine>();
        lines.AddRange(report.Deployments
            .Where(deployment => deployment.InFlight && Own(deployment.Project, deployment.Environment))
            .OrderBy(Urgency)
            .Select(deployment => InFlight(deployment, now)));
        lines.AddRange(Freezes(report, environment, project, now).Select(freeze => OfFreeze(freeze, now, zone, Brief(freeze, now, zone))));
        if (report.Recent.FirstOrDefault(recent => Own(recent.Project, recent.Environment)) is { } last)
        {
            lines.Add(Last(last, now, zone, Brief(last, now)));
        }

        return [.. lines.Take(MostLines).Select(line => line with { Text = Cut(line.Text, NodeLength) })];
    }

    /// <summary>
    /// What follows the marks under an environment's name, <see cref="MostLines"/> at most: the freeze that covers
    /// the environment (the one in force before the one that comes; the tooltip names every one), and the last
    /// deployment to it that ended, unless a mark already says that one. The lines name the project. Empty where the
    /// file has neither.
    /// </summary>
    /// <param name="marks">The marks shown above the lines (<see cref="DeploymentsReport.Marks"/>).</param>
    public static IReadOnlyList<ActivityLine> OfEnvironment(DeploymentsReport report, string environment, IReadOnlyList<DeploymentMark> marks, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(zone);
        var lines = new List<ActivityLine>();
        var freezes = Freezes(report, environment, null, now);
        if (freezes.Count > 0)
        {
            var first = freezes[0];
            var more = freezes.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $", and {freezes.Count - 1} more") : string.Empty;
            var words = first.InForceAt(now)
                ? $"deployment freeze {first.Name}: {Names(first.Projects)} frozen until {When(first.To, now, zone)}{more}"
                : $"deployment freeze {first.Name}: {Names(first.Projects)} frozen from {When(first.From!.Value, now, zone)} until {When(first.To, now, zone)}{more}";
            lines.Add(OfFreeze(first, now, zone, words) with { Title = string.Join('\n', freezes.Select(freeze => FreezeTitle(freeze, now, zone))) });
        }

        var last = report.Recent.FirstOrDefault(recent => string.Equals(recent.Environment, environment, StringComparison.OrdinalIgnoreCase));
        if (last is not null && !marks.Any(mark => !mark.InFlight && string.Equals(mark.Project, last.Project, StringComparison.OrdinalIgnoreCase) && mark.Release == last.Release))
        {
            var by = last.StartedBy is { } startedBy ? $", started by {startedBy}" : string.Empty;
            lines.Add(Last(last, now, zone, $"last: {Sentence(last, now)}{by}"));
        }

        return lines;
    }

    /// <summary>The freezes that cover the environment (and the project, when one is asked for) and are in force or coming: in force first, then by their start.</summary>
    private static List<DeploymentFreeze> Freezes(DeploymentsReport report, string environment, string? project, DateTimeOffset now) =>
        [.. report.Freezes
            .Where(freeze => freeze.Covers(environment, project) && (freeze.InForceAt(now) || freeze.ComesAt(now)))
            .OrderBy(freeze => freeze.InForceAt(now) ? 0 : 1)
            .ThenBy(freeze => freeze.From ?? DateTimeOffset.MinValue)];

    private static int Urgency(Deployment deployment) => deployment.State switch
    {
        DeploymentState.Waiting => 0,
        DeploymentState.Executing => 1,
        DeploymentState.Queued => 2,
        _ => 3,
    };

    private static ActivityLine InFlight(Deployment deployment, DateTimeOffset now)
    {
        var age = deployment.Since is { } since ? DeploymentText.Age(now - since) : null;
        var sentence = DeploymentText.Sentence(deployment, now);
        var title = DeploymentText.Detail(deployment, now) is { } detail ? $"{sentence} ({detail})" : sentence;
        switch (deployment.State)
        {
            case DeploymentState.Waiting:
            {
                var wait = deployment.WaitsFor;
                var waited = wait?.Since is { } asked ? $", {DeploymentText.Age(now - asked)}" : age is null ? string.Empty : $", {age}";
                var words = (wait?.IsGuidedFailure ?? false, wait?.Responsible) switch
                {
                    (true, { } who) => $"{deployment.Release} failed a step: {who} decides",
                    (true, null) => $"{deployment.Release} failed a step and waits",
                    (false, { } who) => $"{deployment.Release} waits for {who}",
                    _ => $"{deployment.Release} waits for a sign-off{waited}",
                };
                return new ActivityLine(ActivityKind.Waiting, words, title, deployment.Url);
            }

            case DeploymentState.Executing:
            {
                var by = deployment.StartedBy is { } startedBy ? $", by {startedBy}" : string.Empty;
                return new ActivityLine(ActivityKind.Deploying, $"deploying {deployment.Release}{(age is null ? string.Empty : $", {age}")}{by}", title, deployment.Url);
            }

            case DeploymentState.Queued:
                return new ActivityLine(ActivityKind.Queued, $"{deployment.Release} queued{(age is null ? string.Empty : $", {age}")}", title, deployment.Url);

            default:
                return new ActivityLine(ActivityKind.Queued, $"{deployment.Release}: {deployment.StateText}", title, deployment.Url);
        }
    }

    private static ActivityLine OfFreeze(DeploymentFreeze freeze, DateTimeOffset now, TimeZoneInfo zone, string words) =>
        new(freeze.InForceAt(now) ? ActivityKind.Frozen : ActivityKind.Freeze, words, FreezeTitle(freeze, now, zone));

    /// <summary><c>frozen until Sun 19:00: prod weekend</c>, <c>freeze from Sat 19:00: prod weekend</c>.</summary>
    private static string Brief(DeploymentFreeze freeze, DateTimeOffset now, TimeZoneInfo zone) =>
        freeze.InForceAt(now)
            ? $"frozen until {When(freeze.To, now, zone)}: {freeze.Name}"
            : $"freeze from {When(freeze.From!.Value, now, zone)}: {freeze.Name}";

    private static string FreezeTitle(DeploymentFreeze freeze, DateTimeOffset now, TimeZoneInfo zone)
    {
        var from = freeze.From is { } begins ? $" from {TimeText.DateAndTime(begins, zone)}" : string.Empty;
        var state = freeze.InForceAt(now)
            ? "In force now"
            : $"Begins in {TimeText.Span(freeze.From!.Value - now)}";
        return $"Deployment freeze {freeze.Name}: no deployment of {Names(freeze.Projects)} to {Names(freeze.Environments)}{from} until {TimeText.DateAndTime(freeze.To, zone)}. {state}. "
            + "A freeze belongs to the Octopus Deploy instance; a person with the right may override it for one deployment.";
    }

    private static ActivityLine Last(RecentDeployment last, DateTimeOffset now, TimeZoneInfo zone, string words)
    {
        var by = last.StartedBy is { } startedBy ? $", started by {startedBy}" : string.Empty;
        return new ActivityLine(
            last.Result switch
            {
                DeploymentState.Succeeded => ActivityKind.Deployed,
                DeploymentState.Failed => ActivityKind.Failed,
                _ => ActivityKind.Canceled,
            },
            words,
            $"The last deployment of {last.Project} to {last.Environment} that ended: {Sentence(last, now)} ({TimeText.DateAndTime(last.Finished, zone)}){by}.",
            last.Url);
    }

    /// <summary><c>2.4.43 deployed 5 h ago by ai-ops</c>, <c>2.4.43 failed 5 h ago by ai-ops</c>, <c>2.4.43 canceled 5 h ago</c>.</summary>
    private static string Brief(RecentDeployment last, DateTimeOffset now)
    {
        var by = last.StartedBy is { } startedBy ? $" by {startedBy}" : string.Empty;
        var result = last.Result switch
        {
            DeploymentState.Succeeded => "deployed",
            DeploymentState.Failed => "failed",
            DeploymentState.Canceled => "canceled",
            _ => last.ResultText,
        };
        return $"{last.Release} {result} {DeploymentText.Age(now - last.Finished)} ago{by}";
    }

    /// <summary>The words of a mark that ended, for the last deployment: <c>cmdemo2-ui 2.4.43 reached uat 5 h ago</c>.</summary>
    private static string Sentence(RecentDeployment last, DateTimeOffset now)
    {
        var what = $"{last.Project} {last.Release}";
        var ago = $"{DeploymentText.Age(now - last.Finished)} ago";
        return last.Result switch
        {
            DeploymentState.Succeeded => $"{what} reached {last.Environment} {ago}",
            DeploymentState.Failed => $"{what} failed in {last.Environment} {ago}",
            DeploymentState.Canceled => $"{what} was canceled in {last.Environment} {ago}",
            _ => $"{what} in {last.Environment}: {last.ResultText}, {ago}",
        };
    }

    /// <summary>
    /// A moment near now, short: the time of day on the same day (<c>19:00</c>), the day of the week within a week
    /// (<c>Sun 19:00</c>), then the date (<c>12 Oct 19:00</c>); in the viewer's time zone.
    /// </summary>
    public static string When(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var days = Math.Abs((local.Date - today.Date).TotalDays);
        return local.ToString(days < 1 ? "HH:mm" : days < 7 ? "ddd HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Names(IReadOnlyList<string> names) => names.Count == 0 ? "the system" : string.Join(", ", names);

    /// <summary>The words cut to what a line of a node holds, with an ellipsis; the tooltip has them in full.</summary>
    public static string Cut(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= length ? text : $"{text[..(length - 1)].TrimEnd()}…";
    }
}
