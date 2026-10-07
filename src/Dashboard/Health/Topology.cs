namespace Dashboard.Health;

/// <summary>The system the dashboard shows: the content of <c>topology.json</c>.</summary>
public sealed record Topology(SystemInfo System, DateTimeOffset? Generated, IReadOnlyList<EnvironmentInfo> Environments);

public sealed record SystemInfo(string Slug, string Name);

public sealed record EnvironmentInfo(string Name, string? Tier, IReadOnlyList<DeployableInfo> Deployables);

/// <summary>One deployable of an environment: its public address (Front Door) and the nodes behind it.</summary>
public sealed record DeployableInfo(
    string Name,
    Uri? FrontDoor,
    string HealthPath,
    string AlivePath,
    string VersionPath,
    IReadOnlyList<NodeInfo> Nodes)
{
    public const string DefaultHealthPath = "/_healthcheck";
    public const string DefaultAlivePath = "/alive";
    public const string DefaultVersionPath = "/_version";

    /// <summary>The path the chosen probe calls on every node of this deployable.</summary>
    public string PathFor(ProbeKind probe) => probe == ProbeKind.Liveness ? AlivePath : HealthPath;
}

/// <summary>One regional node (web app) of a deployable.</summary>
public sealed record NodeInfo(string Name, string? Region, string Role, Uri Url)
{
    public const string PrimaryRole = "primary";
    public const string StandbyRole = "standby";

    public bool IsPrimary => string.Equals(Role, PrimaryRole, StringComparison.OrdinalIgnoreCase);
}
