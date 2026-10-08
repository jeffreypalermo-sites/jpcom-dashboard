using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// What is being deployed: the content of the file at <c>system.deploymentsUrl</c> of the topology
/// (<c>deployments.json</c>, which a workflow of the system repository publishes). The system reads Octopus Deploy
/// once for all of it, and no application reports its own deployment: the deployments that are queued, executing or
/// waiting for a person, and those that ended lately, whatever the environment. Minutes old, never live.
/// </summary>
/// <param name="Generated">When the system read Octopus Deploy. It is not the time of a check.</param>
/// <param name="Deployments">The deployments, in the file's order: in flight first.</param>
public sealed record DeploymentsReport(DateTimeOffset? Generated, IReadOnlyList<Deployment> Deployments)
{
    /// <summary>
    /// How long a deployment that ended stays marked: one of a few minutes can end before the page saw it start.
    /// </summary>
    public static readonly TimeSpan EndedFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What is marked in an environment at <paramref name="now"/>: every deployment in flight, and one that ended
    /// until <see cref="EndedFor"/> after it did. The one a person has to act on comes first (waiting), then
    /// executing and queued, then what ended: failed, canceled, succeeded.
    /// </summary>
    /// <param name="slug">
    /// The system's slug: the Octopus project of a deployable is <c>&lt;slug&gt;-&lt;deployable&gt;</c>, and
    /// <c>&lt;slug&gt;-system</c> is the system's own.
    /// </param>
    /// <param name="deployables">
    /// The deployables a mark may belong to: those of the topology's environment, or those a diagram draws a node of.
    /// </param>
    public IReadOnlyList<DeploymentMark> Marks(string environment, string slug, IEnumerable<string> deployables, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(deployables);
        var known = deployables.ToList();
        return [.. Deployments
            .Where(deployment => string.Equals(deployment.Environment, environment, StringComparison.OrdinalIgnoreCase) && deployment.IsMarkedAt(now))
            .OrderBy(Rank)
            .Select(deployment => DeploymentMark.Of(deployment, slug, known, now))];
    }

    /// <summary>
    /// The file's content; null when it is not a JSON object with a <c>deployments</c> list. An empty list is a
    /// report: nothing is being deployed.
    /// </summary>
    public static DeploymentsReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("deployments", out var list) && list.ValueKind == JsonValueKind.Array
                ? new DeploymentsReport(JsonRead.Time(root, "generated"), [.. JsonRead.Items(root, "deployments").Select(Deployment.Read).OfType<Deployment>()])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A state the page does not know stands after the ones it knows, in flight or ended as the file says.</summary>
    private static int Rank(Deployment deployment) => deployment.State switch
    {
        DeploymentState.Waiting => 0,
        DeploymentState.Executing => 1,
        DeploymentState.Queued => 2,
        DeploymentState.Failed => 4,
        DeploymentState.Canceled => 5,
        DeploymentState.Succeeded => 6,
        _ => deployment.InFlight ? 3 : 7,
    };
}

public enum DeploymentState
{
    /// <summary>A state this page does not know: said in the file's own word.</summary>
    Unknown,

    /// <summary>Waiting behind another task of the environment, or for the instance's task limit.</summary>
    Queued,

    /// <summary>Running now.</summary>
    Executing,

    /// <summary>Stopped for a person: the sign-off, or a question of a guided failure.</summary>
    Waiting,

    Succeeded,

    Failed,

    Canceled,
}

/// <summary>One deployment task of the system's Octopus space.</summary>
/// <param name="Project">
/// The Octopus project: <c>&lt;slug&gt;-&lt;deployable&gt;</c>, and <c>&lt;slug&gt;-system</c> for the system's own
/// release.
/// </param>
/// <param name="StateText">The state in the file's own word.</param>
/// <param name="Since">When it started, or when it was queued while it has not started; null when the file's time does not parse.</param>
/// <param name="Finished">When it ended; null while it has not, and when the file's time does not parse.</param>
/// <param name="Url">The task in Octopus Deploy; null when the file gives no address.</param>
public sealed record Deployment(
    string Project,
    string Environment,
    string Release,
    DeploymentState State,
    string StateText,
    DateTimeOffset? Since,
    DateTimeOffset? Finished,
    Uri? Url)
{
    /// <summary>
    /// True while it has not ended: queued, executing or waiting. A state the page does not know counts while the
    /// file gives it no end.
    /// </summary>
    public bool InFlight => State switch
    {
        DeploymentState.Queued or DeploymentState.Executing or DeploymentState.Waiting => true,
        DeploymentState.Unknown => Finished is null,
        _ => false,
    };

    /// <summary>
    /// In flight, always; ended, until <see cref="DeploymentsReport.EndedFor"/> after its end. One that ended at a
    /// time the page cannot read is not marked: nothing says for how long it should be.
    /// </summary>
    public bool IsMarkedAt(DateTimeOffset now) => InFlight || (Finished is { } ended && now - ended < DeploymentsReport.EndedFor);

    /// <summary>An entry without a project, an environment, a release or a state is left out.</summary>
    internal static Deployment? Read(JsonElement element) =>
        JsonRead.Text(element, "project") is { } project && JsonRead.Text(element, "environment") is { } environment
            && JsonRead.Text(element, "release") is { } release && JsonRead.Text(element, "state") is { } state
            ? new Deployment(
                project,
                environment,
                release,
                StateOf(state),
                state,
                JsonRead.Time(element, "since"),
                JsonRead.Time(element, "finished"),
                JsonRead.Address(element, "url"))
            : null;

    private static DeploymentState StateOf(string state) => state.ToLowerInvariant() switch
    {
        "queued" => DeploymentState.Queued,
        "executing" => DeploymentState.Executing,
        "waiting" => DeploymentState.Waiting,
        "succeeded" => DeploymentState.Succeeded,
        "failed" => DeploymentState.Failed,
        "canceled" => DeploymentState.Canceled,
        _ => DeploymentState.Unknown,
    };
}

/// <summary>
/// One deployment as the page marks it: on the environment it deploys to, and on the tiles and nodes of its
/// deployable when it has one.
/// </summary>
/// <param name="Project">The Octopus project.</param>
/// <param name="Deployable">
/// The deployable the project deploys. Null for the system's own project (<paramref name="OfSystem"/>), which belongs
/// to the environment as a whole and to no application, and for a project that is none of the deployables asked for.
/// </param>
/// <param name="Sentence">The mark in words: the same as on the fleet's dashboard.</param>
/// <param name="Minutes">
/// Whole minutes since it started (or was queued) while it is in flight, since it ended otherwise; null when the
/// file's time does not parse.
/// </param>
/// <param name="Url">The task in Octopus Deploy; null when the file gives no address.</param>
/// <param name="InFlight">True while it has not ended.</param>
/// <param name="OfSystem">True for the system's own release: its infrastructure and configuration.</param>
public sealed record DeploymentMark(
    string Project,
    string? Deployable,
    string Environment,
    string Release,
    DeploymentState State,
    string Sentence,
    int? Minutes,
    Uri? Url,
    bool InFlight,
    bool OfSystem)
{
    internal static DeploymentMark Of(Deployment deployment, string slug, IReadOnlyList<string> deployables, DateTimeOffset now)
    {
        var name = NameOf(deployment.Project, slug);
        var system = name is not null && DeliveryReport.IsSystem(name);
        return new DeploymentMark(
            deployment.Project,
            system ? null : deployables.FirstOrDefault(deployable => string.Equals(deployable, name, StringComparison.OrdinalIgnoreCase)),
            deployment.Environment,
            deployment.Release,
            deployment.State,
            DeploymentText.Sentence(deployment, now),
            (deployment.InFlight ? deployment.Since : deployment.Finished) is { } then ? (int)Math.Max(0, (now - then).TotalMinutes) : null,
            deployment.Url,
            deployment.InFlight,
            system);
    }

    /// <summary>
    /// What a project is called after the system's slug (<c>ui</c> of <c>cmdemo2-ui</c>); null for a project that is
    /// not named so.
    /// </summary>
    public static string? NameOf(string project, string slug)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(slug);
        return slug.Length > 0 && project.Length > slug.Length + 1 && project.StartsWith($"{slug}-", StringComparison.OrdinalIgnoreCase)
            ? project[(slug.Length + 1)..]
            : null;
    }
}

/// <summary>
/// A deployment in the words of the page. The sentences are the fleet's dashboard's, word for word, so a person
/// reads the same on both walls: they name the project and the environment in full.
/// </summary>
public static class DeploymentText
{
    /// <summary>Where a mark's link goes, for its tooltip.</summary>
    public const string TaskTitle = "The deployment's task in Octopus Deploy" + LinkText.OctopusSuffix;

    /// <summary>
    /// <c>deploying cmdemo2-ui 2.4.43 to uat</c>, <c>cmdemo2-ui 2.4.43 is queued for uat</c>, <c>cmdemo2-ui 2.4.43
    /// waits for a sign-off in uat</c>, <c>cmdemo2-ui 2.4.43 reached uat 5 min ago</c> (<c>failed in</c>, <c>was
    /// canceled in</c>); a state the page does not know in the file's own word (<c>cmdemo2-ui 2.4.43 in uat:
    /// paused</c>).
    /// </summary>
    public static string Sentence(Deployment deployment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var what = $"{deployment.Project} {deployment.Release}";
        var where = deployment.Environment;
        var ago = deployment.Finished is { } ended ? $" {Age(now - ended)} ago" : string.Empty;
        return deployment.State switch
        {
            DeploymentState.Queued => $"{what} is queued for {where}",
            DeploymentState.Executing => $"deploying {what} to {where}",
            DeploymentState.Waiting => $"{what} waits for a sign-off in {where}",
            DeploymentState.Succeeded => $"{what} reached {where}{ago}",
            DeploymentState.Failed => $"{what} failed in {where}{ago}",
            DeploymentState.Canceled => $"{what} was canceled in {where}{ago}",
            _ => $"{what} in {where}: {deployment.StateText}",
        };
    }

    /// <summary>
    /// <c>5 min</c> under an hour and never less than <c>1 min</c>, <c>5 h</c> under two days, then <c>3 d</c>.
    /// </summary>
    public static string Age(TimeSpan span) =>
        span.TotalMinutes < 60 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)span.TotalMinutes)} min")
        : span.TotalHours < 48 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays} d");

    /// <summary>
    /// The sentence, and for a deployment in flight for how long it has been so (<c>deploying cmdemo2-ui 2.4.43 to
    /// uat (3 min so far)</c>): a mark's tooltip.
    /// </summary>
    public static string Title(DeploymentMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return mark is { InFlight: true, Minutes: { } minutes } ? $"{mark.Sentence} ({Age(TimeSpan.FromMinutes(minutes))} so far)" : mark.Sentence;
    }

    /// <summary>
    /// The shape of a mark's dot, as the word its class and the runtime payload carry: <c>executing</c> (filled, and
    /// pulsing where motion is welcome), <c>queued</c> (hollow), <c>waiting</c> (a dot in a ring: a person has to
    /// act) and <c>ended</c> (small and still), which is also the shape of a state the page does not know.
    /// </summary>
    public static string Shape(DeploymentState state) => state switch
    {
        DeploymentState.Executing => "executing",
        DeploymentState.Queued => "queued",
        DeploymentState.Waiting => "waiting",
        _ => "ended",
    };

    /// <summary>What the marks are and how old, for the tooltip of a list of them.</summary>
    public static string Help(DateTimeOffset? generated, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var asOf = generated is { } at ? $", as of {TimeText.DateAndTime(at, zone)}" : string.Empty;
        return $"Not live: what the system itself read from Octopus Deploy{asOf}. A deployment that started shows within about one to six minutes "
            + "and its end as soon, one that is only queued within half an hour; one that ended stays marked for ten minutes.";
    }
}
