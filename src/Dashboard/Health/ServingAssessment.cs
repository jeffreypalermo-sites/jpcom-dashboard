namespace Dashboard.Health;

/// <summary>The state of one regional node, as the serving decision needs it.</summary>
public sealed record NodeHealth(string Name, string? Region, bool IsPrimary, HealthState State)
{
    /// <summary>The region, or the node's name when the topology gives no region.</summary>
    public string Label => Region ?? Name;
}

public enum ServingState
{
    /// <summary>The deployable has no nodes.</summary>
    NoNodes,

    /// <summary>A node that could be the serving one has not been checked yet.</summary>
    Pending,

    /// <summary>A primary node is healthy and serves; in a rotation, every healthy primary node does.</summary>
    Primary,

    /// <summary>No primary node is healthy; a standby is, and serves.</summary>
    FailedOver,

    /// <summary>No node is healthy.</summary>
    Down,
}

public enum FrontDoorAgreement
{
    /// <summary>The deployable has no Front Door endpoint.</summary>
    NotPresent,

    /// <summary>The Front Door endpoint or the nodes have not been checked yet.</summary>
    Pending,

    /// <summary>Front Door is healthy exactly when a node is.</summary>
    Agrees,

    /// <summary>Front Door is healthy and no node is, or the other way around.</summary>
    Disagrees,
}

/// <summary>
/// Which node is expected to serve a deployable's traffic: the first healthy node in priority order (primary nodes,
/// then the others, each in the order of the topology), and whether the Front Door endpoint agrees. A deployable with
/// a Front Door endpoint and more than one primary node is a rotation: the Front Door rotates over every healthy
/// primary node, and a standby serves only when no primary node is healthy.
/// </summary>
/// <param name="OnlyNode">
/// The only node of a deployable without a Front Door endpoint (one app at one public address, as in a cluster); null
/// for every other deployable. Nothing can take over from such a node, so its words name no primary, no standby, no
/// region and no failover: it serves, or it does not.
/// </param>
/// <param name="Rotation">
/// The primary nodes the Front Door rotates over, in the order of the topology; null for a deployable that is not a
/// rotation. <see cref="Expected"/> is then the first of them that is healthy.
/// </param>
public sealed record ServingAssessment(
    ServingState State,
    NodeHealth? Expected,
    NodeHealth? FailedPrimary,
    FrontDoorAgreement FrontDoor,
    HealthState? FrontDoorState,
    NodeHealth? OnlyNode = null,
    IReadOnlyList<NodeHealth>? Rotation = null)
{
    /// <summary>How many nodes that left a rotation the headline names before it counts the rest.</summary>
    public const int MostNamed = 3;

    public bool IsFailedOver => State == ServingState.FailedOver;

    /// <summary>True when the Front Door rotates over several primary nodes.</summary>
    public bool IsRotation => Rotation is not null;

    /// <summary>
    /// The nodes expected to serve the traffic: every healthy primary node of a rotation, and otherwise the one node
    /// <see cref="Expected"/> names (none when nothing serves or nothing is decided).
    /// </summary>
    public IReadOnlyList<NodeHealth> Serving =>
        Rotation is not null && State == ServingState.Primary ? [.. Rotation.Where(node => node.State == HealthState.Healthy)]
        : Expected is null ? []
        : [Expected];

    /// <summary>The primary nodes of a rotation that are not healthy, in the order of the topology; none without a rotation.</summary>
    public IReadOnlyList<NodeHealth> OutOfRotation =>
        Rotation is null ? [] : [.. Rotation.Where(node => node.State != HealthState.Healthy)];

    public string Headline => Rotation is { } rotation
        ? State switch
        {
            ServingState.Pending => "Checking which regions serve traffic",
            ServingState.Primary => RotationHeadline(rotation.Count, OutOfRotation),
            ServingState.FailedOver => $"Failed over to {Expected!.Label}",
            _ => "No region is healthy",
        }
        : OnlyNode is { } only
        ? State switch
        {
            ServingState.Pending => $"Checking {only.Label}",
            ServingState.Primary or ServingState.FailedOver => $"Serves traffic: {only.Label}",
            _ => $"Not serving: {only.Label}",
        }
        : State switch
        {
            ServingState.NoNodes => "No nodes in the topology",
            ServingState.Pending => "Checking which region serves traffic",
            ServingState.Primary => $"Expected to serve traffic: {Expected!.Label} (primary)",
            ServingState.FailedOver => $"Failed over to {Expected!.Label}",
            _ => "No healthy node: nothing can serve traffic",
        };

    /// <summary>Why the deployable failed over; null in every other state, and for a single node.</summary>
    public string? FailoverDetail => State != ServingState.FailedOver || OnlyNode is not null
        ? null
        : FailedPrimary is null
            ? $"No primary node is healthy; {Expected!.Label} is expected to serve traffic."
            : $"Primary {FailedPrimary.Label} is {Lower(FailedPrimary.State)}; {Expected!.Label} is expected to serve traffic.";

    /// <summary>Whether the Front Door endpoint shows what the nodes predict; null without a Front Door endpoint.</summary>
    public string? FrontDoorText => FrontDoor switch
    {
        FrontDoorAgreement.NotPresent => null,
        FrontDoorAgreement.Pending => "Front Door is being checked.",
        FrontDoorAgreement.Agrees when FrontDoorState == HealthState.Healthy => "Front Door agrees: it is healthy.",
        FrontDoorAgreement.Agrees => $"Front Door agrees: it is {Lower(FrontDoorState)} too.",
        _ when FrontDoorState == HealthState.Healthy => "Front Door disagrees: it is healthy although no node is.",
        _ => $"Front Door disagrees: it is {Lower(FrontDoorState)} although {Expected?.Label ?? "a node"} is healthy.",
    };

    public static ServingAssessment Assess(IEnumerable<NodeHealth> nodes, HealthState? frontDoor)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // OrderBy is stable: within primaries and within standbys, the order of the topology is the priority.
        var byPriority = nodes.OrderBy(node => node.IsPrimary ? 0 : 1).ToList();
        var primaries = byPriority.Where(node => node.IsPrimary).ToList();
        if (frontDoor is not null && primaries.Count > 1)
        {
            // No primary is named as the failed one: the detail says that none is healthy.
            var (rotating, first) = DecideRotation(primaries, byPriority);
            return new ServingAssessment(rotating, first, null, Compare(rotating, frontDoor), frontDoor, null, primaries);
        }

        var (state, expected) = Decide(byPriority);
        var failedPrimary = state == ServingState.FailedOver ? byPriority.FirstOrDefault(node => node.IsPrimary) : null;
        var single = frontDoor is null && byPriority.Count == 1 ? byPriority[0] : null;
        return new ServingAssessment(state, expected, failedPrimary, Compare(state, frontDoor), frontDoor, single);
    }

    /// <summary>
    /// A rotation is decided when every primary node is checked: the healthy ones serve. With none healthy, the
    /// standbys are taken in the order of the topology, as for one primary.
    /// </summary>
    private static (ServingState State, NodeHealth? Expected) DecideRotation(List<NodeHealth> primaries, List<NodeHealth> byPriority)
    {
        if (primaries.Any(node => node.State == HealthState.Pending))
        {
            return (ServingState.Pending, null);
        }

        return primaries.FirstOrDefault(node => node.State == HealthState.Healthy) is { } healthy
            ? (ServingState.Primary, healthy)
            : Decide(byPriority);
    }

    /// <summary><c>Front Door rotates over 10 of 11 regions: wus3 is not healthy</c>.</summary>
    private static string RotationHeadline(int count, IReadOnlyList<NodeHealth> stopped)
    {
        if (stopped.Count == 0)
        {
            return $"Front Door rotates over {count} regions: all healthy";
        }

        var names = stopped.Take(MostNamed).Select(node => node.Label).ToList();
        if (stopped.Count > MostNamed)
        {
            names.Add($"{stopped.Count - MostNamed} more");
        }

        var listed = names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return $"Front Door rotates over {count - stopped.Count} of {count} regions: {listed} {(stopped.Count == 1 ? "is" : "are")} not healthy";
    }

    private static (ServingState State, NodeHealth? Expected) Decide(List<NodeHealth> byPriority)
    {
        if (byPriority.Count == 0)
        {
            return (ServingState.NoNodes, null);
        }

        foreach (var node in byPriority)
        {
            if (node.State == HealthState.Healthy)
            {
                return (node.IsPrimary ? ServingState.Primary : ServingState.FailedOver, node);
            }

            // A node with a higher priority than every healthy one is still unchecked: no decision yet.
            if (node.State == HealthState.Pending)
            {
                return (ServingState.Pending, null);
            }
        }

        return (ServingState.Down, null);
    }

    private static FrontDoorAgreement Compare(ServingState state, HealthState? frontDoor)
    {
        if (frontDoor is null)
        {
            return FrontDoorAgreement.NotPresent;
        }

        if (frontDoor == HealthState.Pending || state is ServingState.Pending or ServingState.NoNodes)
        {
            return FrontDoorAgreement.Pending;
        }

        var nodeServes = state is ServingState.Primary or ServingState.FailedOver;
        return nodeServes == (frontDoor == HealthState.Healthy) ? FrontDoorAgreement.Agrees : FrontDoorAgreement.Disagrees;
    }

    private static string Lower(HealthState? state) => state switch
    {
        HealthState.Healthy => "healthy",
        HealthState.Unhealthy => "unhealthy",
        HealthState.Unreachable => "unreachable",
        _ => "not checked",
    };
}
