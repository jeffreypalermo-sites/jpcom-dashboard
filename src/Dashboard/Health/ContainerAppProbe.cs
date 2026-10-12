using System.Globalization;

namespace Dashboard.Health;

/// <summary>
/// A container app the runtime view draws, as this page probes it: its public address and, for an app
/// <c>system.json</c> declares, the health path of its deployable. An app nobody declared has none: the page asks its
/// root, and an answer says that it responds, never that it is healthy.
/// </summary>
public sealed record ContainerAppTarget(Uri Url, string? HealthPath = null)
{
    public const string Root = "/";

    /// <summary>The path a probe asks: the health path with the probe Health check, the root otherwise.</summary>
    public string PathFor(ProbeKind probe) => ReadsHealth(probe) ? HealthPath! : Root;

    /// <summary>True when a probe of that kind asks the health path, so the status of its answer is the app's health.</summary>
    public bool ReadsHealth(ProbeKind probe) => probe == ProbeKind.Health && HealthPath is not null;

    internal string Key => $"{Url.AbsoluteUri}|{HealthPath}";
}

/// <summary>What the runtime view and the page's checks need to know about the container apps it shows.</summary>
/// <param name="Apps">The container apps the page probes while it shows them.</param>
/// <param name="ResourceHealth">True when a box of the diagram shows what Resource Health said of its resources.</param>
public sealed record RuntimeWatch(IReadOnlyList<ContainerAppTarget> Apps, bool ResourceHealth)
{
    public static RuntimeWatch None { get; } = new([], false);
}

public enum ContainerAppState
{
    /// <summary>Not asked yet.</summary>
    Probing,

    /// <summary>No answer yet, within <see cref="ContainerAppProbe.Grace"/> of the first probe: an app that scaled to zero starts.</summary>
    Waking,

    /// <summary>It answered, and the page cannot tell its health from the answer.</summary>
    Responding,

    /// <summary>Its health path answered HTTP 200.</summary>
    Healthy,

    /// <summary>Its health path answered another status.</summary>
    Unhealthy,

    /// <summary>No answer, after the grace or after it had answered.</summary>
    NotResponding,
}

/// <summary>One probe of a container app.</summary>
/// <param name="Path">The path it asked.</param>
/// <param name="ReadsHealth">True when that path is the app's health path.</param>
/// <param name="StatusCode">The HTTP status, when the browser could read the answer.</param>
/// <param name="Opaque">
/// True when the app answered and the browser could not read the answer: the app does not allow this page's origin
/// (CORS), so the page asked without reading it.
/// </param>
/// <param name="LatencyMs">Time to the answer.</param>
/// <param name="CheckedAt">When the probe started.</param>
/// <param name="Detail">Why there is no answer.</param>
public sealed record ContainerAppAnswer(
    string Path,
    bool ReadsHealth,
    int? StatusCode,
    bool Opaque,
    int? LatencyMs,
    DateTimeOffset CheckedAt,
    string? Detail = null)
{
    public bool Answered => StatusCode is not null || Opaque;
}

/// <summary>A container app's state as the page last saw it.</summary>
/// <param name="Last">The last probe; null before the first.</param>
/// <param name="CorsRefused">True once the app answered without allowing this page's origin.</param>
public sealed record ContainerAppStatus(ContainerAppState State, ContainerAppAnswer? Last = null, bool CorsRefused = false)
{
    public static ContainerAppStatus NotYet { get; } = new(ContainerAppState.Probing);
}

/// <summary>
/// The page's probes of one container app: one every <see cref="Interval"/> while the runtime view shows it, never
/// while no page is open. A probe after <see cref="Asleep"/> without one (the page was hidden, paused or closed) starts
/// a new run: the app may have scaled to zero, and it is <see cref="ContainerAppState.Waking"/> until it answers or
/// <see cref="Grace"/> has passed since the run's first probe.
/// </summary>
public sealed class ContainerAppProbe(ContainerAppTarget target)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);

    /// <summary>The default time a container app waits without a request before it scales to zero.</summary>
    public static readonly TimeSpan Asleep = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _runStartedAt;
    private DateTimeOffset? _lastProbeAt;
    private bool _answeredInRun;

    public ContainerAppTarget Target { get; } = target;

    public ContainerAppStatus Status { get; private set; } = ContainerAppStatus.NotYet;

    /// <summary>True before the first probe.</summary>
    public bool IsNew => _lastProbeAt is null;

    public bool IsDue(DateTimeOffset now) => _lastProbeAt is not { } last || now - last >= Interval;

    /// <summary>Marks a probe as started, so the next round does not ask again before the interval.</summary>
    public void Start(DateTimeOffset now)
    {
        if (_lastProbeAt is not { } last || now - last >= Asleep)
        {
            _runStartedAt = now;
            _answeredInRun = false;
        }

        _lastProbeAt = now;
    }

    public void Record(ContainerAppAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answeredInRun |= answer.Answered;
        var state = answer switch
        {
            { StatusCode: { } status, ReadsHealth: true } => HealthClassifier.FromStatusCode(status) == HealthState.Healthy
                ? ContainerAppState.Healthy
                : ContainerAppState.Unhealthy,
            { Answered: true } => ContainerAppState.Responding,
            _ when !_answeredInRun && answer.CheckedAt - (_runStartedAt ?? answer.CheckedAt) < Grace => ContainerAppState.Waking,
            _ => ContainerAppState.NotResponding,
        };
        Status = new ContainerAppStatus(state, answer, Status.CorsRefused || answer.Opaque);
    }
}

/// <summary>A container app's probe in words: the badge, the facts next to it and the tooltip.</summary>
public static class ContainerAppText
{
    public static string Label(ContainerAppState state) => state switch
    {
        ContainerAppState.Waking => "Waking",
        ContainerAppState.Responding => "Responding",
        ContainerAppState.Healthy => "Healthy",
        ContainerAppState.Unhealthy => "Unhealthy",
        ContainerAppState.NotResponding => "Not responding",
        _ => "Probing",
    };

    /// <summary>Next to the badge: <c>HTTP 200 · 85 ms</c>, <c>answered · 85 ms</c>, or why there is no answer.</summary>
    public static string Facts(ContainerAppStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Last switch
        {
            null => "not probed yet",
            { StatusCode: { } code, LatencyMs: { } ms } => string.Create(CultureInfo.InvariantCulture, $"HTTP {code} · {ms} ms"),
            { StatusCode: { } code } => string.Create(CultureInfo.InvariantCulture, $"HTTP {code}"),
            { Opaque: true, LatencyMs: { } ms } => string.Create(CultureInfo.InvariantCulture, $"answered · {ms} ms"),
            { Opaque: true } => "answered",
            _ when status.State == ContainerAppState.Waking => "no answer yet",
            _ => "no answer",
        };
    }

    /// <summary>The tooltip: what the page asks, how often, what it found and what it cannot see.</summary>
    public static string Title(string name, ContainerAppTarget target, ContainerAppStatus status, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(zone);
        var words = new List<string>
        {
            $"{name} ({target.Url.Host}): {Label(status.State)}.",
            "This page asks it once a minute while it shows this diagram, and not while it is paused or hidden; nothing asks it from here while no page is open, so it can scale to zero.",
        };
        if (status.Last is { } last)
        {
            var when = TimeText.Clock(last.CheckedAt, zone);
            words.Add(last switch
            {
                { StatusCode: { } code } => string.Create(CultureInfo.InvariantCulture, $"Last probe {when}: {last.Path} answered HTTP {code}."),
                { Opaque: true } => $"Last probe {when}: {last.Path} answered. The app does not allow this page's origin (CORS), so the page cannot read its answer or its health; it asks without reading the answer.",
                _ => $"Last probe {when}: {last.Path}, no answer. {last.Detail}".TrimEnd(),
            });
            if (last.Answered && !last.ReadsHealth)
            {
                words.Add(target.HealthPath is null
                    ? "It names no health path (an app system.json does not declare): the page asks its root, and an answer says it responds, not that it is healthy."
                    : "The probe Liveness asks its root and leaves its health check (and a database it queries) alone.");
            }
        }

        if (status.State is ContainerAppState.Probing or ContainerAppState.Waking)
        {
            words.Add($"An app that scaled to zero starts on the first request: the page waits up to {ContainerAppProbe.Grace.TotalSeconds:0} seconds for its first answer before it says Not responding.");
        }

        return string.Join(' ', words);
    }
}
