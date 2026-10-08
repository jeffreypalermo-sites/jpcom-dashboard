using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// How the system is delivered: the content of the file at <c>system.deliveryUrl</c> of the topology
/// (<c>delivery.json</c>, which a workflow of the system repository publishes). Per environment and deployable the
/// last deployment, per environment what its hourly health reports found, and for the system the last failover test.
/// Every part is optional.
/// </summary>
/// <param name="Generated">When the file's content last changed: the facts are as of then. It is not the time of a check.</param>
public sealed record DeliveryReport(DateTimeOffset? Generated, IReadOnlyList<DeliveryEnvironment> Environments, FailoverTest? Failover)
{
    /// <summary>The entry of a deployable in an environment; null when the file has none.</summary>
    public DeliveryEntry? Find(string environment, string deployable) =>
        Environments
            .FirstOrDefault(entry => string.Equals(entry.Name, environment, StringComparison.OrdinalIgnoreCase))
            ?.Deployables.FirstOrDefault(entry => string.Equals(entry.Name, deployable, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entries of an environment that belong to no deployable the page shows: the system project itself (named
    /// <see cref="SystemName"/>: the infrastructure and the pipeline) first, then the others in the file's order, such
    /// as the dashboard.
    /// </summary>
    public IReadOnlyList<DeliveryEntry> Others(string environment, IEnumerable<string> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var names = shown.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = Environments
            .FirstOrDefault(entry => string.Equals(entry.Name, environment, StringComparison.OrdinalIgnoreCase))
            ?.Deployables.Where(entry => !names.Contains(entry.Name)) ?? [];
        return [.. entries.OrderBy(entry => IsSystem(entry.Name) ? 0 : 1)];
    }

    /// <summary>What the hourly health reports of an environment found; null when the file does not say.</summary>
    public HealthReports? HealthOf(string environment) =>
        Environments.FirstOrDefault(entry => string.Equals(entry.Name, environment, StringComparison.OrdinalIgnoreCase))?.Health;

    /// <summary>The name of the entry of the system project itself.</summary>
    public const string SystemName = "system";

    public static bool IsSystem(string name) => string.Equals(name, SystemName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's content; null when it is not a JSON object with <c>environments</c> or <c>failover</c>.</summary>
    public static DeliveryReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var environments = JsonRead.Items(root, "environments")
                .Select(environment => (Name: JsonRead.Text(environment, "name"), Element: environment))
                .Where(environment => environment.Name is not null)
                .Select(environment => new DeliveryEnvironment(
                    environment.Name!,
                    [.. JsonRead.Items(environment.Element, "deployables").Select(DeliveryEntry.Read).OfType<DeliveryEntry>()],
                    HealthReports.Read(JsonRead.Section(environment.Element, "health"))))
                .ToList();
            var failover = FailoverTest.Read(JsonRead.Section(root, "failover"));
            return environments.Count == 0 && failover is null ? null : new DeliveryReport(JsonRead.Time(root, "generated"), environments, failover);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="Health">What the environment's hourly health reports found (<c>health</c>); null when the file does not say.</param>
public sealed record DeliveryEnvironment(string Name, IReadOnlyList<DeliveryEntry> Deployables, HealthReports? Health = null);

/// <summary>
/// What the pipeline's hourly health report found in an environment (<c>health</c> of an environment in
/// <c>delivery.json</c>): the runbook asks every node once an hour and fails when one is not healthy. A count of
/// hourly checks, not continuous monitoring: an outage between two checks is not in it.
/// </summary>
/// <param name="Last24Hours">The reports of the last 24 hours.</param>
/// <param name="Last7Days">The reports of the last seven days.</param>
/// <param name="LastFailure">When the last report that failed ended; null when none did in seven days.</param>
public sealed record HealthReports(HealthWindow? Last24Hours, HealthWindow? Last7Days, DateTimeOffset? LastFailure)
{
    internal static HealthReports? Read(JsonElement? section)
    {
        var day = HealthWindow.Read(JsonRead.Section(section, "last24Hours"));
        var week = HealthWindow.Read(JsonRead.Section(section, "last7Days"));
        return day is null && week is null ? null : new HealthReports(day, week, JsonRead.Time(section, "lastFailure"));
    }
}

/// <param name="Reports">How many health reports ended in the window.</param>
/// <param name="Healthy">How many of them found every node healthy.</param>
public sealed record HealthWindow(int Reports, int Healthy)
{
    public bool AllHealthy => Healthy == Reports;

    /// <summary>The window; null unless both counts are there and no more reports are healthy than there were.</summary>
    internal static HealthWindow? Read(JsonElement? section) =>
        JsonRead.Count(section, "reports") is { } reports && JsonRead.Count(section, "healthy") is { } healthy && healthy <= reports
            ? new HealthWindow(reports, healthy)
            : null;
}

/// <summary>The last deployment of a deployable to an environment.</summary>
/// <param name="Version">The version it deployed.</param>
/// <param name="DeployedAt">When it ended.</param>
/// <param name="SignedOffBy">Who signed it off.</param>
/// <param name="Reason">The reason given with the sign-off.</param>
/// <param name="Commit">The commit of the version.</param>
/// <param name="CommitAt">When that commit was made.</param>
/// <param name="LeadTimeHours">From the commit to this deployment.</param>
/// <param name="Behind">How far the environment is behind the first one.</param>
/// <param name="DeploymentsLast7Days">Deployments of the deployable to the environment in the last seven days.</param>
/// <param name="FailedLast7Days">Of those, the failed ones.</param>
/// <param name="ReleaseUrl">The release's page.</param>
public sealed record DeliveryEntry(
    string Name,
    string? Version,
    DateTimeOffset? DeployedAt,
    string? SignedOffBy,
    string? Reason,
    string? Commit,
    DateTimeOffset? CommitAt,
    double? LeadTimeHours,
    BehindFirst? Behind,
    int? DeploymentsLast7Days,
    int? FailedLast7Days,
    Uri? ReleaseUrl)
{
    internal static DeliveryEntry? Read(JsonElement element)
    {
        if (JsonRead.Text(element, "name") is not { } name)
        {
            return null;
        }

        var behind = JsonRead.Section(element, "behindFirst");
        return new DeliveryEntry(
            name,
            VersionText.Display(JsonRead.Text(element, "version")),
            JsonRead.Time(element, "deployedAt"),
            JsonRead.Text(element, "signedOffBy"),
            JsonRead.Text(element, "reason"),
            JsonRead.Text(element, "commit"),
            JsonRead.Time(element, "commitAt"),
            JsonRead.Number(element, "leadTimeHours"),
            behind is null ? null : new BehindFirst(JsonRead.Count(behind, "versions"), JsonRead.Number(behind, "days")),
            JsonRead.Count(element, "deploymentsLast7Days"),
            JsonRead.Count(element, "failedLast7Days"),
            JsonRead.Address(element, "releaseUrl"));
    }
}

/// <param name="Versions">How many releases the first environment is ahead, in the project's list of releases.</param>
/// <param name="Days">
/// Days since this environment and the first one last ran the same release; null when they never did.
/// </param>
public sealed record BehindFirst(int? Versions, double? Days);

/// <summary>The last test of the failover to the standby region.</summary>
/// <param name="Seconds">How long the public address took to answer from the standby.</param>
public sealed record FailoverTest(string? Environment, DateTimeOffset? At, double? Seconds)
{
    internal static FailoverTest? Read(JsonElement? section)
    {
        var test = new FailoverTest(JsonRead.Text(section, "environment"), JsonRead.Time(section, "at"), JsonRead.Number(section, "seconds"));
        return test == new FailoverTest(null, null, null) ? null : test;
    }
}

/// <summary>
/// The hourly health reports in the words of the page: <c>Healthy in 23 of 24 hourly checks (95.8 %) in 24 hours ·
/// 164 of 166 in 7 days · last failure 9 h ago</c>.
/// </summary>
public static class AvailabilityText
{
    /// <summary>Said next to the numbers, so nobody reads them as monitoring.</summary>
    public const string Note = "Hourly checks by the pipeline, not continuous monitoring.";

    /// <summary>The tooltip: who counts, what a check is, and what the count misses.</summary>
    public static string Help(DateTimeOffset? changed, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        const string What = "Counted by the pipeline: once an hour the runbook \"Health report\" in Octopus Deploy asks every node of the environment and its public address, and the report fails when one is not healthy. It is no continuous monitoring: an outage between two reports is not counted.";
        return changed is { } at ? $"{What} As of the last change of the delivery facts: {TimeText.DateAndTime(at, zone)}." : What;
    }

    /// <summary>
    /// The parts of the line, in order: the last 24 hours, the last seven days, the last failure. A part the file
    /// does not have is left out; none at all is an empty list.
    /// </summary>
    public static IReadOnlyList<AvailabilityPart> Parts(HealthReports reports, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var parts = new List<AvailabilityPart>();
        if (reports.Last24Hours is { } day)
        {
            parts.Add(day.Reports == 0
                ? new AvailabilityPart("No hourly check ended in ", "24 hours", string.Empty)
                : new AvailabilityPart("Healthy in ", Count(day), $" hourly {(day.Reports == 1 ? "check" : "checks")} ({Percent(day)}) in 24 hours"));
        }

        if (reports.Last7Days is { } week)
        {
            // After the 24 hours the words are short; alone, the part says what it counts.
            parts.Add((parts.Count > 0, week.Reports) switch
            {
                (true, 0) => new AvailabilityPart(string.Empty, "none", " in 7 days"),
                (true, _) => new AvailabilityPart(string.Empty, Count(week), " in 7 days", $"{Percent(week)} of the hourly checks of the last 7 days"),
                (false, 0) => new AvailabilityPart("No hourly check ended in ", "7 days", string.Empty),
                _ => new AvailabilityPart("Healthy in ", Count(week), $" hourly {(week.Reports == 1 ? "check" : "checks")} ({Percent(week)}) in 7 days"),
            });
        }

        if (reports.LastFailure is { } failed)
        {
            parts.Add(new AvailabilityPart("last failure ", TimeText.Ago(failed, now), string.Empty, When: failed));
        }
        else if (reports.Last7Days is { Reports: > 0, AllHealthy: true })
        {
            parts.Add(new AvailabilityPart("no failure in ", "7 days", string.Empty));
        }

        return parts;
    }

    /// <summary>The whole line in one text, its parts joined with a dot.</summary>
    public static string Line(HealthReports reports, DateTimeOffset now) =>
        string.Join(" · ", Parts(reports, now).Select(part => $"{part.Before}{part.Strong}{part.After}"));

    /// <summary><c>23 of 24</c>.</summary>
    public static string Count(HealthWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return string.Create(CultureInfo.InvariantCulture, $"{window.Healthy} of {window.Reports}");
    }

    /// <summary>
    /// <c>95.8 %</c>, <c>100 %</c>: one decimal, and never 100 while a report failed (<c>99.9 %</c>) nor 0 while one
    /// passed (<c>0.1 %</c>).
    /// </summary>
    public static string Percent(HealthWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Reports == 0)
        {
            return "–";
        }

        var percent = Math.Round(100.0 * window.Healthy / window.Reports, 1);
        percent = window.Healthy < window.Reports ? Math.Min(percent, 99.9) : percent;
        percent = window.Healthy > 0 ? Math.Max(percent, 0.1) : percent;
        return string.Create(CultureInfo.InvariantCulture, $"{percent:0.#} %");
    }
}

/// <summary>One part of the availability line: words, the number in the middle that carries it, words.</summary>
/// <param name="Title">The part's own tooltip; null without one.</param>
/// <param name="When">The moment the part names, for a <c>time</c> element; null when it names none.</param>
public sealed record AvailabilityPart(string Before, string Strong, string After, string? Title = null, DateTimeOffset? When = null);

/// <summary>The delivery facts in the words of the "Delivery" card.</summary>
public static class DeliveryText
{
    /// <summary><c>5.2 h</c>; under an hour <c>40 min</c>; from two days on <c>3.5 d</c>.</summary>
    public static string LeadTime(double hours) =>
        hours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(hours * 60):0} min")
        : hours < 48 ? string.Create(CultureInfo.InvariantCulture, $"{hours:0.#} h")
        : string.Create(CultureInfo.InvariantCulture, $"{hours / 24:0.#} d");

    /// <summary>Whose delivery a card shows: <c>ui in uat</c>; the system project by what it is.</summary>
    public static string Context(string name, string environment) =>
        DeliveryReport.IsSystem(name) ? $"the system (infrastructure and pipeline) in {environment}" : $"{name} in {environment}";

    /// <summary>What "behind" counts, for the tooltip.</summary>
    public static string BehindHelp(string first) =>
        $"Versions: the distance in the project's list of releases. Days: since this environment and {first} last ran the same release.";

    /// <summary>
    /// <c>same as tdd</c>, <c>2 versions, 3 days behind tdd</c>; null for the first environment itself and when the
    /// file does not say.
    /// </summary>
    public static string? Behind(BehindFirst? behind, string environment, string? first)
    {
        if (behind is null || first is null || string.Equals(environment, first, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (behind.Versions == 0)
        {
            return $"same as {first}";
        }

        var parts = new List<string>();
        if (behind.Versions is { } versions && versions > 0)
        {
            parts.Add(versions == 1 ? "1 version" : string.Create(CultureInfo.InvariantCulture, $"{versions} versions"));
        }

        if (behind.Days is { } days && Math.Round(days) >= 1)
        {
            var whole = (int)Math.Round(days);
            parts.Add(whole == 1 ? "1 day" : string.Create(CultureInfo.InvariantCulture, $"{whole} days"));
        }

        return parts.Count > 0 ? $"{string.Join(", ", parts)} behind {first}" : behind.Days is not null ? $"same as {first}" : null;
    }

    /// <summary><c>4 deployments, none failed</c>; null when the file does not count them.</summary>
    public static string? Frequency(DeliveryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DeploymentsLast7Days is not { } count)
        {
            return null;
        }

        var deployments = count == 1 ? "1 deployment" : string.Create(CultureInfo.InvariantCulture, $"{count} deployments");
        return entry.FailedLast7Days switch
        {
            null => deployments,
            0 => count == 0 ? deployments : $"{deployments}, none failed",
            var failed => string.Create(CultureInfo.InvariantCulture, $"{deployments}, {failed} failed"),
        };
    }

    /// <summary><c>uat, 17 h ago: traffic moved to the standby in 44 s</c>.</summary>
    public static string Failover(FailoverTest test, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(test);
        var parts = new List<string>();
        if (test.Environment is { } environment)
        {
            parts.Add(environment);
        }

        if (test.At is { } at)
        {
            parts.Add(TimeText.Ago(at, now));
        }

        var text = string.Join(", ", parts);
        if (test.Seconds is { } seconds)
        {
            var took = string.Create(CultureInfo.InvariantCulture, $"the standby answered after {seconds:0.#} s");
            text = text.Length > 0 ? $"{text}: {took}" : took;
        }

        return text;
    }
}
