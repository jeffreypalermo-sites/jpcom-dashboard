using System.Net;

namespace Dashboard.Tests;

public class ContainerAppProbeTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly Uri App = new("https://ca-demo-tdd-ui.graybush-1.southcentralus.azurecontainerapps.io");
    private static readonly ContainerAppTarget Declared = new(App, "/_healthcheck");
    private static readonly ContainerAppTarget Undeclared = new(App);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly SignallingTimeProvider _time = new();

    private static ContainerAppAnswer NoAnswer(DateTimeOffset at) => new("/_healthcheck", true, null, false, null, at, "No answer within 10 s.");

    private static ContainerAppAnswer Status(int status, DateTimeOffset at, bool readsHealth = true) => new(readsHealth ? "/_healthcheck" : "/", readsHealth, status, false, 85, at);

    private static ContainerAppAnswer Unread(DateTimeOffset at) => new("/", false, null, true, 85, at);

    /// <summary>A probe started and recorded at <paramref name="seconds"/> after the page opened.</summary>
    private static void Probe(ContainerAppProbe probe, int seconds, Func<DateTimeOffset, ContainerAppAnswer> answer)
    {
        var at = Opened.AddSeconds(seconds);
        probe.Start(at);
        probe.Record(answer(at));
    }

    /// <summary>True for a request the browser makes without reading the answer (fetch mode no-cors).</summary>
    private static bool Unreadable(HttpRequestMessage request) =>
        request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var options)
        && options.TryGetValue("mode", out var mode) && Equals(mode, "no-cors");

    [Fact]
    public void BeforeTheFirstProbeTheAppIsBeingProbedAndDue()
    {
        var probe = new ContainerAppProbe(Declared);

        Assert.Equal(ContainerAppStatus.NotYet, probe.Status);
        Assert.True(probe.IsNew);
        Assert.True(probe.IsDue(Opened));
    }

    [Fact]
    public void AnAppThatDoesNotAnswerIsWakingForNinetySecondsThenNotResponding()
    {
        var probe = new ContainerAppProbe(Declared);

        Probe(probe, 0, NoAnswer);
        Assert.Equal(ContainerAppState.Waking, probe.Status.State);
        Assert.False(probe.IsDue(Opened.AddSeconds(59)));
        Assert.True(probe.IsDue(Opened.AddSeconds(60)));

        Probe(probe, 60, NoAnswer);
        Assert.Equal(ContainerAppState.Waking, probe.Status.State);

        Probe(probe, 120, NoAnswer);
        Assert.Equal(ContainerAppState.NotResponding, probe.Status.State);
    }

    [Fact]
    public void AWakingAppThatAnswersIsHealthyByItsHealthPath()
    {
        var probe = new ContainerAppProbe(Declared);

        Probe(probe, 0, NoAnswer);
        Probe(probe, 60, at => Status(200, at));
        Assert.Equal(ContainerAppState.Healthy, probe.Status.State);

        Probe(probe, 120, at => Status(503, at));
        Assert.Equal(ContainerAppState.Unhealthy, probe.Status.State);
    }

    [Fact]
    public void AnAppThatAnsweredAndStopsAnsweringIsNotRespondingAtOnce()
    {
        var probe = new ContainerAppProbe(Declared);

        Probe(probe, 0, at => Status(200, at));
        Probe(probe, 60, NoAnswer);

        Assert.Equal(ContainerAppState.NotResponding, probe.Status.State);
    }

    [Fact]
    public void AfterFiveMinutesWithoutAProbeTheAppMayBeAsleepAgainAndIsWakingOnceMore()
    {
        var probe = new ContainerAppProbe(Declared);
        Probe(probe, 0, at => Status(200, at));

        // The tab was hidden: no probe for six minutes.
        Probe(probe, 360, NoAnswer);

        Assert.Equal(ContainerAppState.Waking, probe.Status.State);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(503)]
    public void AnAnswerToTheRootIsRespondingNeverHealthy(int status)
    {
        var probe = new ContainerAppProbe(Undeclared);

        Probe(probe, 0, at => Status(status, at, readsHealth: false));

        Assert.Equal(ContainerAppState.Responding, probe.Status.State);
    }

    [Fact]
    public void AnAnswerThePageMayNotReadIsRespondingAndRemembered()
    {
        var probe = new ContainerAppProbe(Declared);

        Probe(probe, 0, Unread);
        Assert.Equal(new ContainerAppStatus(ContainerAppState.Responding, Unread(Opened), true), probe.Status);

        Probe(probe, 60, NoAnswer);
        Assert.True(probe.Status.CorsRefused);
    }

    [Fact]
    public void TheHealthPathIsAskedWithTheProbeHealthCheckAndTheRootOtherwise()
    {
        Assert.Equal("/_healthcheck", Declared.PathFor(ProbeKind.Health));
        Assert.Equal("/", Declared.PathFor(ProbeKind.Liveness));
        Assert.Equal("/", Undeclared.PathFor(ProbeKind.Health));
        Assert.False(Undeclared.ReadsHealth(ProbeKind.Health));
    }

    [Fact]
    public async Task AnAppThatAllowsThePagesOriginIsReadWithOneRequest()
    {
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK));

        var answer = await new NodeProber(new HttpClient(handler), _time).ProbeContainerAppAsync(Declared, ProbeKind.Health, false, CancellationToken.None);

        Assert.Equal(("/_healthcheck", true, (int?)200, false), (answer.Path, answer.ReadsHealth, answer.StatusCode, answer.Opaque));
        Assert.Equal([$"{App.AbsoluteUri}_healthcheck"], handler.RequestedUrls);
        Assert.False(Unreadable(handler.Requests[0]));
    }

    [Fact]
    public async Task AnAppThatRefusesThePagesOriginIsAskedAgainWithoutReadingAndAnswers()
    {
        var handler = new StubHandler(request => Unreadable(request) ? StubHandler.Answer(HttpStatusCode.OK) : throw new HttpRequestException("TypeError: Failed to fetch"));

        var answer = await new NodeProber(new HttpClient(handler), _time).ProbeContainerAppAsync(Declared, ProbeKind.Health, false, CancellationToken.None);

        Assert.Equal(((int?)null, true, true), (answer.StatusCode, answer.Opaque, answer.Answered));
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(Unreadable(handler.Requests[1]));
    }

    [Fact]
    public async Task OnceRefusedItIsAskedWithoutReadingAtOnceWithOneRequest()
    {
        var handler = new StubHandler(request => Unreadable(request) ? StubHandler.Answer(HttpStatusCode.OK) : throw new HttpRequestException("TypeError: Failed to fetch"));

        var answer = await new NodeProber(new HttpClient(handler), _time).ProbeContainerAppAsync(Declared, ProbeKind.Health, true, CancellationToken.None);

        Assert.True(answer.Opaque);
        Assert.True(Unreadable(Assert.Single(handler.Requests)));
    }

    [Fact]
    public async Task NoAnswerEitherWayIsNoAnswerWithTheReason()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("TypeError: Failed to fetch"));

        var answer = await new NodeProber(new HttpClient(handler), _time).ProbeContainerAppAsync(Undeclared, ProbeKind.Health, false, CancellationToken.None);

        Assert.False(answer.Answered);
        Assert.Equal("/", answer.Path);
        Assert.Contains("no answer", answer.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppThatHangsIsNoAnswerAfterTheTimeout()
    {
        var handler = new StubHandler((_, cancellationToken) => StubHandler.NeverAsync(cancellationToken));

        var probe = new NodeProber(new HttpClient(handler), _time).ProbeContainerAppAsync(Declared, ProbeKind.Health, false, CancellationToken.None);
        await _time.TimerCreatedAsync();
        _time.Advance(NodeProber.DefaultTimeout);
        var answer = await probe.WaitAsync(Patience);

        Assert.False(answer.Answered);
        Assert.Equal("No answer within 10 s.", answer.Detail);
    }

    private const string WithDelivery = """
        { "system": { "slug": "demo", "name": "Demo", "deliveryUrl": "https://raw.githubusercontent.com/example-org/demo-system/status/delivery.json" },
          "environments": [ { "name": "tdd", "deployables": [] } ] }
        """;

    private DashboardMonitor Monitor(StubHandler handler) =>
        new(TopologyParser.Parse(WithDelivery).Topology!, new NodeProber(new HttpClient(handler), _time), new PinnedVersionsReader(new HttpClient(handler), _time), _time);

    private static bool IsApp(HttpRequestMessage request) => request.RequestUri!.Host == App.Host;

    private static HttpResponseMessage Answer(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/example-org/demo-system/status/containers.json" => StubHandler.Answer(HttpStatusCode.OK, """
            { "resources": { "nonprod": [ { "id": "/s/kv", "health": { "status": "available", "source": "resourceHealth", "checkedAt": "2026-10-04T21:20:00Z" } } ] } }
            """),
        _ when IsApp(request) => StubHandler.Answer(HttpStatusCode.OK),
        _ => StubHandler.Answer(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task ThePageProbesTheAppsItShowsOnceAMinuteAndNothingElse()
    {
        var handler = new StubHandler(Answer);
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, IsApp);
        Assert.Null(monitor.ContainerAppStatusOf(Declared));

        Assert.True(monitor.Watch(new RuntimeWatch([Declared], false)));
        Assert.Equal(ContainerAppState.Probing, monitor.ContainerAppStatusOf(Declared)!.State);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(ContainerAppState.Healthy, monitor.ContainerAppStatusOf(Declared)!.State);

        _time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Single(handler.Requests, IsApp);

        _time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count(IsApp));

        // The runtime view no longer shows it: it is not asked again, and what was seen of it stays.
        Assert.False(monitor.Watch(RuntimeWatch.None));
        _time.Advance(TimeSpan.FromMinutes(2));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count(IsApp));
        Assert.Equal(ContainerAppState.Healthy, monitor.ContainerAppStatusOf(Declared)!.State);
        Assert.False(monitor.Watch(new RuntimeWatch([Declared], false)));
    }

    [Fact]
    public void TheSameWatchAgainIsNotDueAgainSoAPausedPageIsNotAskedForEveryRendering()
    {
        var monitor = Monitor(new StubHandler(Answer));

        Assert.True(monitor.Watch(new RuntimeWatch([Declared], true)));
        Assert.False(monitor.Watch(new RuntimeWatch([Declared], true)));
        Assert.True(monitor.Watch(new RuntimeWatch([Declared, Undeclared], true)));
        Assert.False(monitor.Watch(RuntimeWatch.None));
    }

    [Fact]
    public async Task WithTheProbeLivenessTheAppIsAskedAtItsRoot()
    {
        var handler = new StubHandler(Answer);
        var monitor = Monitor(handler);
        monitor.Watch(new RuntimeWatch([Declared], false));

        await monitor.CheckAllAsync(ProbeKind.Liveness, CancellationToken.None);

        Assert.Equal("/", handler.Requests.Single(IsApp).RequestUri!.AbsolutePath);
        Assert.Equal(ContainerAppState.Responding, monitor.ContainerAppStatusOf(Declared)!.State);
    }

    [Fact]
    public async Task ResourceHealthIsReadNextToTheDeliveryFactsOnlyWhileABoxShowsIt()
    {
        var handler = new StubHandler(Answer);
        var monitor = Monitor(handler);
        static bool IsContainers(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/containers.json", StringComparison.Ordinal);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, IsContainers);
        Assert.Null(monitor.ResourceHealth);

        Assert.True(monitor.Watch(new RuntimeWatch([], true)));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal("https://raw.githubusercontent.com/example-org/demo-system/status/containers.json", handler.Requests.Single(IsContainers).RequestUri!.AbsoluteUri);
        Assert.Equal("available", monitor.ResourceHealth?.Find("/S/KV")?.Status);
        Assert.False(monitor.Watch(new RuntimeWatch([], true)));
    }

    [Fact]
    public async Task ARoundWithNothingWatchedRaisesNoMoreChanges()
    {
        var monitor = Monitor(new StubHandler(Answer));
        var changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None).WaitAsync(Patience);

        // The delivery facts (404 here) change nothing; the end of the round is the one change.
        Assert.Equal(1, changes);
    }
}
