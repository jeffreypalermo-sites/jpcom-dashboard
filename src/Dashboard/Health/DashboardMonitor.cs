namespace Dashboard.Health;

/// <summary>The state of every endpoint of a topology, and the action that checks them all.</summary>
public sealed class DashboardMonitor
{
    private readonly NodeProber _prober;
    private readonly TimeProvider _time;
    private readonly List<(DeployableStatus Deployable, TargetStatus Target)> _targets;

    public DashboardMonitor(Topology topology, NodeProber prober, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(topology);
        Topology = topology;
        _prober = prober;
        _time = time;
        Environments =
        [
            .. topology.Environments.Select(environment => new EnvironmentStatus(
                environment.Name,
                environment.Tier,
                [.. environment.Deployables.Select(deployable => new DeployableStatus(deployable))])),
        ];
        _targets =
        [
            .. from environment in Environments
               from deployable in environment.Deployables
               from target in deployable.Targets
               select (deployable, target),
        ];
    }

    /// <summary>Raised whenever an endpoint's state changed and when a round of checks ended.</summary>
    public event Action? Changed;

    public Topology Topology { get; }

    public IReadOnlyList<EnvironmentStatus> Environments { get; }

    public IEnumerable<TargetStatus> Targets => _targets.Select(entry => entry.Target);

    public HealthSummary Summary => HealthSummary.Of(Targets.Select(target => target.State));

    /// <summary>When the last round of checks ended; null before the first one.</summary>
    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>
    /// Checks every endpoint at the same time. Each result is recorded as it arrives, so a node that hangs until its
    /// timeout delays neither the others nor their display.
    /// </summary>
    public async Task CheckAllAsync(ProbeKind probe, CancellationToken cancellationToken)
    {
        await Task.WhenAll(_targets.Select(entry => CheckAsync(entry.Deployable.Info, entry.Target, probe, cancellationToken)));
        LastRefresh = _time.GetUtcNow();
        Changed?.Invoke();
    }

    private async Task CheckAsync(DeployableInfo deployable, TargetStatus target, ProbeKind probe, CancellationToken cancellationToken)
    {
        var result = await _prober.ProbeAsync(target.Url, deployable.PathFor(probe), deployable.VersionPath, cancellationToken);
        target.Record(result);
        Changed?.Invoke();
    }
}
