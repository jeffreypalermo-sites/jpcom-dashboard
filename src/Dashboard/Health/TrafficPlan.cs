namespace Dashboard.Health;

/// <summary>
/// The requests the traffic button sends in one environment: every deployable's representative paths
/// (<c>trafficPaths</c> of the topology; the start page without them) at its public address, which is the Front Door
/// endpoint where there is one and the primary web app's own address otherwise. So the calls take the path a user's
/// calls take: browser, Front Door, the origin that serves, the database.
/// </summary>
public sealed record TrafficPlan(string Environment, IReadOnlyList<Uri> Addresses, IReadOnlyList<string> Targets)
{
    public static readonly IReadOnlyList<string> DefaultPaths = ["/"];

    /// <summary>Requests per second while the button runs: enough to move every number, far below any limit.</summary>
    public const int PerSecond = 2;

    /// <summary>How long one press sends: the counters' window is one minute.</summary>
    public const int Seconds = 60;

    /// <summary>How often the page checks while the traffic runs, so the numbers follow it.</summary>
    public const int CheckEverySeconds = 10;

    /// <summary>
    /// The environment the panel's choice shows, among those that have something to call. The viewer's own choice
    /// stays, and so does everything while traffic runs; otherwise the choice follows the environment the page shows
    /// (the runtime view's), and is the first one where the page shows none. Null when no environment has a plan.
    /// </summary>
    /// <param name="current">What the choice shows now; null before the first decision.</param>
    /// <param name="shown">The environment the page shows, or last showed; null while it has shown none.</param>
    /// <param name="chosenByViewer">True once the viewer picked <paramref name="current"/> in the panel.</param>
    public static string? Choose(IReadOnlyList<TrafficPlan> plans, string? current, string? shown, bool chosenByViewer, bool running)
    {
        ArgumentNullException.ThrowIfNull(plans);
        string? Known(string? name) =>
            name is null ? null : plans.FirstOrDefault(plan => string.Equals(plan.Environment, name, StringComparison.OrdinalIgnoreCase))?.Environment;

        if (running)
        {
            return current;
        }

        return (chosenByViewer ? Known(current) : null) ?? Known(shown) ?? (plans.Count > 0 ? plans[0].Environment : null);
    }

    /// <summary>
    /// What stands next to the interval control while traffic runs: the page then checks every
    /// <see cref="CheckEverySeconds"/> s, whatever the control says. Null when no traffic runs, and when the chosen
    /// interval is no longer than that (the control is then right as it is).
    /// </summary>
    public static string? IntervalNote(bool running, TimeSpan interval) =>
        running && interval > TimeSpan.FromSeconds(CheckEverySeconds) ? $"every {CheckEverySeconds} s while traffic runs" : null;

    /// <summary>The plan of an environment; null when it has nothing to call.</summary>
    public static TrafficPlan? For(EnvironmentInfo environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var addresses = new List<Uri>();
        var targets = new List<string>();
        foreach (var deployable in environment.Deployables)
        {
            var publicAddress = deployable.FrontDoor
                ?? deployable.Nodes.FirstOrDefault(node => node.IsPrimary)?.Url
                ?? (deployable.Nodes.Count > 0 ? deployable.Nodes[0].Url : null);
            // An empty list of traffic paths is said on purpose: a deployable that takes no generated traffic (a
            // dashboard the topology lists as a node). Without the key, the start page is called.
            if (publicAddress is null || deployable.TrafficPaths is { Count: 0 })
            {
                continue;
            }

            targets.Add($"{deployable.Name} at {publicAddress.Host}");
            var paths = deployable.TrafficPaths is { Count: > 0 } configured ? configured : DefaultPaths;
            addresses.AddRange(paths.Select(path => ProbeUrl.Combine(publicAddress, path)));
        }

        return addresses.Count == 0 ? null : new TrafficPlan(environment.Name, addresses, targets);
    }
}
