namespace Dashboard.Tests;

/// <summary>
/// A deployable with a Front Door endpoint and more than one primary node: the Front Door rotates over every healthy
/// primary, and a standby serves only when no primary is healthy.
/// </summary>
public class RotationTests
{
    private const HealthState Healthy = HealthState.Healthy;
    private const HealthState Unhealthy = HealthState.Unhealthy;
    private const HealthState Unreachable = HealthState.Unreachable;
    private const HealthState Pending = HealthState.Pending;

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("http://localhost:5210/");
    private static readonly string[] Regions = ["westus3", "eastus2", "centralus", "northeurope", "uksouth"];

    private static NodeHealth Primary(HealthState state, string region) => new($"app-{region}", region, true, state);

    private static NodeHealth Standby(HealthState state, string region = "japaneast") => new($"app-{region}", region, false, state);

    /// <summary>One primary per state, in the regions of <see cref="Regions"/>.</summary>
    private static NodeHealth[] Primaries(params HealthState[] states) => [.. states.Select((state, index) => Primary(state, Regions[index]))];

    private static ServingAssessment Rotation(HealthState[] primaries, HealthState? standby = null, HealthState? frontDoor = Healthy) =>
        ServingAssessment.Assess(standby is { } state ? [.. Primaries(primaries), Standby(state)] : Primaries(primaries), frontDoor);

    private static string[] Names(IEnumerable<NodeHealth> nodes) => [.. nodes.Select(node => node.Label)];

    [Fact]
    public void EveryHealthyPrimaryServes()
    {
        var assessment = Rotation([Healthy, Healthy, Healthy], standby: Healthy);

        Assert.Equal(ServingState.Primary, assessment.State);
        Assert.True(assessment.IsRotation);
        Assert.Equal(["westus3", "eastus2", "centralus"], Names(assessment.Serving));
        Assert.Empty(assessment.OutOfRotation);
        Assert.Equal("westus3", assessment.Expected?.Region);
        Assert.Equal("Front Door rotates over 3 regions: all healthy", assessment.Headline);
        Assert.Null(assessment.FailoverDetail);
        Assert.Equal("Front Door agrees: it is healthy.", assessment.FrontDoorText);
    }

    [Fact]
    public void ElevenRegionsAreCounted()
    {
        var nodes = Enumerable.Range(1, 11).Select(index => Primary(index == 4 ? Unhealthy : Healthy, $"region{index}")).ToList();

        Assert.Equal("Front Door rotates over 10 of 11 regions: region4 is not healthy", ServingAssessment.Assess(nodes, Healthy).Headline);
        Assert.Equal(10, ServingAssessment.Assess(nodes, Healthy).Serving.Count);
    }

    [Theory]
    [InlineData(1, "Front Door rotates over 4 of 5 regions: westus3 is not healthy")]
    [InlineData(2, "Front Door rotates over 3 of 5 regions: westus3 and eastus2 are not healthy")]
    [InlineData(3, "Front Door rotates over 2 of 5 regions: westus3, eastus2 and centralus are not healthy")]
    [InlineData(4, "Front Door rotates over 1 of 5 regions: westus3, eastus2, centralus and 1 more are not healthy")]
    public void TheHeadlineNamesUpToThreeNodesThatAreNotHealthy(int stopped, string headline)
    {
        var assessment = Rotation([.. Enumerable.Range(0, 5).Select(index => index < stopped ? Unreachable : Healthy)]);

        Assert.Equal(ServingState.Primary, assessment.State);
        Assert.Equal(headline, assessment.Headline);
        Assert.Equal(5 - stopped, assessment.Serving.Count);
        Assert.Equal(stopped, assessment.OutOfRotation.Count);
        Assert.Equal(FrontDoorAgreement.Agrees, assessment.FrontDoor);
    }

    [Fact]
    public void AnUnhealthyPrimaryDoesNotServeAndTheStandbyStillStandsBy()
    {
        var assessment = Rotation([Healthy, Unhealthy, Healthy], standby: Healthy);

        Assert.Equal(["westus3", "centralus"], Names(assessment.Serving));
        Assert.Equal(["eastus2"], Names(assessment.OutOfRotation));
        Assert.False(assessment.IsFailedOver);
    }

    [Fact]
    public void WithNoPrimaryHealthyTheFirstHealthyStandbyServesAndThatIsAFailover()
    {
        NodeHealth[] nodes = [.. Primaries(Unreachable, Unhealthy), Standby(Unhealthy, "japaneast"), Standby(Healthy, "koreacentral"), Standby(Healthy, "australiaeast")];

        var assessment = ServingAssessment.Assess(nodes, Healthy);

        Assert.Equal(ServingState.FailedOver, assessment.State);
        Assert.True(assessment.IsFailedOver);
        Assert.Equal(["koreacentral"], Names(assessment.Serving));
        Assert.Equal("Failed over to koreacentral", assessment.Headline);
        Assert.Equal("No primary node is healthy; koreacentral is expected to serve traffic.", assessment.FailoverDetail);
        Assert.Equal(FrontDoorAgreement.Agrees, assessment.FrontDoor);
    }

    [Fact]
    public void WithNoNodeHealthyNothingServes()
    {
        var assessment = Rotation([Unreachable, Unhealthy], standby: Unreachable, frontDoor: Healthy);

        Assert.Equal(ServingState.Down, assessment.State);
        Assert.Empty(assessment.Serving);
        Assert.Equal("No region is healthy", assessment.Headline);
        Assert.Equal("Front Door disagrees: it is healthy although no node is.", assessment.FrontDoorText);
        Assert.Equal("No region is healthy", Rotation([Unreachable, Unhealthy]).Headline);
    }

    [Fact]
    public void TheFrontDoorEndpointShouldBeHealthyWhenAtLeastOnePrimaryIs()
    {
        var assessment = Rotation([Unreachable, Healthy, Unreachable], frontDoor: Unreachable);

        Assert.Equal(FrontDoorAgreement.Disagrees, assessment.FrontDoor);
        Assert.Equal("Front Door disagrees: it is unreachable although eastus2 is healthy.", assessment.FrontDoorText);
        Assert.Equal(FrontDoorAgreement.Agrees, Rotation([Unreachable, Unhealthy], frontDoor: Unhealthy).FrontDoor);
    }

    [Fact]
    public void ARotationIsDecidedWhenEveryPrimaryIsChecked()
    {
        var assessment = Rotation([Healthy, Pending, Healthy]);

        Assert.Equal(ServingState.Pending, assessment.State);
        Assert.Empty(assessment.Serving);
        Assert.Equal("Checking which regions serve traffic", assessment.Headline);
        Assert.Equal(ServingState.Primary, Rotation([Healthy, Healthy], standby: Pending).State);
        Assert.Equal(ServingState.Pending, Rotation([Unhealthy, Unhealthy], standby: Pending).State);
    }

    [Fact]
    public void SeveralPrimariesWithoutAFrontDoorEndpointAreReadAsBefore()
    {
        var assessment = Rotation([Healthy, Healthy, Healthy], standby: Healthy, frontDoor: null);

        Assert.False(assessment.IsRotation);
        Assert.Equal(ServingState.Primary, assessment.State);
        Assert.Equal(["westus3"], Names(assessment.Serving));
        Assert.Equal("Expected to serve traffic: westus3 (primary)", assessment.Headline);

        // The second primary takes over from the first, as the next node in priority order.
        var second = Rotation([Unreachable, Healthy, Healthy], frontDoor: null);
        Assert.Equal(["eastus2"], Names(second.Serving));
        Assert.Equal("Expected to serve traffic: eastus2 (primary)", second.Headline);
    }

    [Fact]
    public void OnePrimaryBehindAFrontDoorIsNoRotation()
    {
        var assessment = ServingAssessment.Assess([Primary(Healthy, "westus3"), Standby(Healthy)], Healthy);

        Assert.False(assessment.IsRotation);
        Assert.Empty(assessment.OutOfRotation);
        Assert.Equal(["westus3"], Names(assessment.Serving));
        Assert.Equal("Expected to serve traffic: westus3 (primary)", assessment.Headline);
        Assert.Empty(ServingAssessment.Assess([Primary(Unreachable, "westus3"), Standby(Unreachable)], Unreachable).Serving);
    }

    private static string[] Events(ServingAssessment? before, ServingAssessment after) =>
        [.. EventDetector.ServingChanges(before, after, "prod", "ui", Now).Select(entry => $"{entry.Level}: {entry.Text}")];

    [Fact]
    public void APrimaryThatLeavesTheRotationAndComesBackIsNoFailover()
    {
        var all = Rotation([Healthy, Healthy, Healthy], standby: Healthy);
        var two = Rotation([Healthy, Unreachable, Healthy], standby: Healthy);

        var left = Assert.Single(EventDetector.ServingChanges(all, two, "prod", "ui", Now));

        Assert.Equal(new DashboardEvent(Now, EventKind.Serving, EventLevel.Warning, "prod", "ui", "eastus2 left the rotation: 2 of 3 regions serve traffic."), left);
        Assert.Equal(["Good: eastus2 is back in the rotation: 3 of 3 regions serve traffic."], Events(two, all));

        // The first primary is one of several: when it goes, no serving region changes and nothing fails over.
        Assert.Equal(["Warning: westus3 left the rotation: 2 of 3 regions serve traffic."], Events(all, Rotation([Unhealthy, Healthy, Healthy], standby: Healthy)));
        Assert.Empty(Events(two, two));
        Assert.Empty(Events(two, Rotation([Healthy, Unhealthy, Healthy], standby: Unreachable)));
    }

    [Fact]
    public void EveryNodeThatLeftOrCameBackInOneRoundHasItsEvent() =>
        Assert.Equal(
            ["Good: westus3 is back in the rotation: 1 of 3 regions serve traffic.", "Warning: eastus2 left the rotation: 1 of 3 regions serve traffic.", "Warning: centralus left the rotation: 1 of 3 regions serve traffic."],
            Events(Rotation([Unreachable, Healthy, Healthy]), Rotation([Healthy, Unreachable, Unhealthy])));

    [Fact]
    public void AFailoverIsTheMoveFromThePrimariesToAStandby()
    {
        var one = Rotation([Unreachable, Healthy], standby: Healthy);
        var failedOver = Rotation([Unreachable, Unreachable], standby: Healthy);

        Assert.Equal(["Warning: Failover: the rotation → japaneast. No primary node is healthy; japaneast is expected to serve traffic."], Events(one, failedOver));
        Assert.Equal(["Good: Failback: japaneast → the rotation. 1 of 2 regions serve traffic."], Events(failedOver, one));
        Assert.Empty(Events(failedOver, failedOver));
    }

    [Fact]
    public void NothingServingAndTheRotationServingAgainAreEvents()
    {
        var all = Rotation([Healthy, Healthy], standby: Unreachable);
        var down = Rotation([Unreachable, Unreachable], standby: Unreachable);

        Assert.Equal(["Problem: No healthy node: the rotation no longer serves, and nothing else can."], Events(all, down));
        Assert.Equal(["Good: The rotation serves traffic again: 2 of 2 regions serve traffic."], Events(down, all));
        Assert.Equal(["Warning: Failed over to japaneast. No primary node is healthy; japaneast is expected to serve traffic."], Events(down, Rotation([Unreachable, Unreachable], standby: Healthy)));
        Assert.Equal(["Problem: No healthy node: japaneast no longer serves, and nothing else can."], Events(Rotation([Unreachable, Unreachable], standby: Healthy), down));
    }

    [Fact]
    public void TheFirstDecisionOfARotationIsAnEventOnlyWhenNoPrimaryServes()
    {
        var pending = Rotation([Pending, Pending]);

        Assert.Empty(Events(null, Rotation([Healthy, Healthy])));
        Assert.Empty(Events(pending, Rotation([Healthy, Unreachable])));
        Assert.Empty(Events(Rotation([Healthy, Healthy]), pending));
        Assert.Equal(["Problem: No healthy node: nothing can serve traffic."], Events(pending, Rotation([Unreachable, Unreachable])));
        Assert.Equal(["Warning: Failed over to japaneast. No primary node is healthy; japaneast is expected to serve traffic."], Events(null, Rotation([Unreachable, Unreachable], standby: Healthy)));
    }

    [Fact]
    public void ADeployableThatIsNoRotationKeepsItsOneEvent()
    {
        var primary = ServingAssessment.Assess([Primary(Healthy, "westus3"), Standby(Healthy, "eastus2")], Healthy);
        var failedOver = ServingAssessment.Assess([Primary(Unreachable, "westus3"), Standby(Healthy, "eastus2")], Healthy);

        Assert.Equal(["Warning: Failover: westus3 → eastus2. Primary westus3 is unreachable; eastus2 is expected to serve traffic."], Events(primary, failedOver));
        Assert.Equal(["Good: Failback: eastus2 → westus3. The primary is healthy again."], Events(failedOver, primary));
        Assert.Empty(Events(primary, primary));

        // Several primaries without a Front Door endpoint: the serving region changes, as before.
        Assert.Equal(["Info: Serving region: westus3 → eastus2."], Events(Rotation([Healthy, Healthy], frontDoor: null), Rotation([Unreachable, Healthy], frontDoor: null)));
    }

    private const string Topology = """
        { "environments": [ { "name": "prod", "deployables": [ {
            "name": "ui", "frontDoor": "https://fd-prod.example.net",
            "nodes": [
              { "name": "prod-west", "region": "westus3", "role": "primary", "url": "https://prod-west.example.net" },
              { "name": "prod-east", "region": "eastus2", "role": "primary", "url": "https://prod-east.example.net" },
              { "name": "prod-north", "region": "northeurope", "role": "primary", "url": "https://prod-north.example.net" },
              { "name": "prod-japan", "region": "japaneast", "role": "standby", "url": "https://prod-japan.example.net" } ] } ] } ] }
        """;

    private static readonly string[] Apps = ["west", "east", "north", "japan"];

    /// <summary>Front Door in front of three primary web apps and a standby, each in a region of its own.</summary>
    private static readonly RuntimeManifest Manifest = new(
        "prod",
        [
            new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
            new RuntimeNode("fd_ui", RuntimeNodeKind.FrontDoor, "fd-prod", new Uri("https://fd-prod.example.net"), "ui"),
            new RuntimeNode("app_ui_west", RuntimeNodeKind.WebApp, "prod-west", new Uri("https://prod-west.example.net"), "ui", "primary", "westus3", "region_west"),
            new RuntimeNode("app_ui_east", RuntimeNodeKind.WebApp, "prod-east", new Uri("https://prod-east.example.net"), "ui", "primary", "eastus2", "region_east"),
            new RuntimeNode("app_ui_north", RuntimeNodeKind.WebApp, "prod-north", new Uri("https://prod-north.example.net"), "ui", "primary", "northeurope", "region_north"),
            new RuntimeNode("app_ui_japan", RuntimeNodeKind.WebApp, "prod-japan", new Uri("https://prod-japan.example.net"), "ui", "standby", "japaneast", "region_japan"),
        ],
        [
            new RuntimeRegion("region_west", "westus3", ["primary"]),
            new RuntimeRegion("region_east", "eastus2", ["primary"]),
            new RuntimeRegion("region_north", "northeurope", ["primary"]),
            new RuntimeRegion("region_japan", "japaneast", ["standby"]),
        ],
        [
            new RuntimeEdge("browser-to-fd_ui", "browser", "fd_ui", RuntimeEdgeKind.Public),
            new RuntimeEdge("fd_ui-to-app_ui_west", "fd_ui", "app_ui_west", RuntimeEdgeKind.Origin, 1),
            new RuntimeEdge("fd_ui-to-app_ui_east", "fd_ui", "app_ui_east", RuntimeEdgeKind.Origin, 1),
            new RuntimeEdge("fd_ui-to-app_ui_north", "fd_ui", "app_ui_north", RuntimeEdgeKind.Origin, 1),
            new RuntimeEdge("fd_ui-to-app_ui_japan", "fd_ui", "app_ui_japan", RuntimeEdgeKind.Origin, 2),
        ]);

    private static ProbeResult Answer(HealthState state) => new(state, state == Unreachable ? null : state == Healthy ? 200 : 503, 40, Now, "2.4.21");

    /// <summary>The runtime view of prod with the nodes in the given states, in the order west, east, north, japan.</summary>
    private static RuntimePayload Payload(params HealthState[] states)
    {
        var prod = new EnvironmentStatus(TopologyParser.Parse(Topology).Topology!.Environments[0]);
        var ui = prod.Deployables[0];
        ui.FrontDoor!.Record(Answer(states.Contains(Healthy) ? Healthy : Unhealthy));
        foreach (var (state, index) in states.Select((state, index) => (state, index)))
        {
            ui.Nodes[index].Record(Answer(state));
        }

        return RuntimePayloadBuilder.Build(Manifest, prod, Page, TimeZoneInfo.Utc);
    }

    private static (string Region, string Line, string Words)[] Drawn(RuntimePayload payload) =>
        [.. Apps.Select(app => (
            payload.Regions.Single(region => region.Alias == $"region_{app}").State,
            payload.Edges.Single(edge => edge.Id == $"fd_ui-to-app_ui_{app}").State,
            payload.Nodes.Single(tile => tile.Alias == $"app_ui_{app}").Lines[^1].Text))];

    private static string FrontDoorLine(RuntimePayload payload) => payload.Nodes.Single(tile => tile.Alias == "fd_ui").Lines[1].Text;

    [Fact]
    public void TheRuntimeViewShowsEveryHealthyPrimaryServing()
    {
        var payload = Payload(Healthy, Healthy, Healthy, Healthy);

        Assert.Equal(
            [("serving", "active", "primary: serves traffic"), ("serving", "active", "primary: serves traffic"), ("serving", "active", "primary: serves traffic"),
             ("standby", "idle", "standby: ready, no traffic")],
            Drawn(payload));
        Assert.All(payload.Regions.Take(3), region => Assert.Equal("serving traffic", region.Label));
        Assert.Equal("rotates over 3 regions", FrontDoorLine(payload));
        Assert.Equal("serving", payload.Nodes.Single(tile => tile.Alias == "app_ui_east").Lines[^1].Tone);
        Assert.Equal("active", payload.Edges.Single(edge => edge.Id == "browser-to-fd_ui").State);

        var origin = payload.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_east");
        Assert.Equal("in the rotation, while healthy", origin.Text);
        Assert.StartsWith("Front Door to app_ui_east, origin priority 1: in the rotation, while healthy. Carries the traffic.", origin.Title, StringComparison.Ordinal);
        Assert.Equal("when priority 1 is down", payload.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_japan").Text);
    }

    [Fact]
    public void TheRuntimeViewShowsAPrimaryThatLeftTheRotationAsNotServing()
    {
        var payload = Payload(Healthy, Unreachable, Healthy, Healthy);

        Assert.Equal(
            [("serving", "active", "primary: serves traffic"), ("down", "down", "primary: not serving"), ("serving", "active", "primary: serves traffic"),
             ("standby", "idle", "standby: ready, no traffic")],
            Drawn(payload));
        Assert.Equal("rotates over 2 of 3 regions", FrontDoorLine(payload));
    }

    [Fact]
    public void TheRuntimeViewShowsTheFailoverToTheStandbyWhenNoPrimaryIsHealthy()
    {
        var payload = Payload(Unreachable, Unhealthy, Unreachable, Healthy);

        Assert.Equal(
            [("down", "down", "primary: not serving"), ("down", "down", "primary: not serving"), ("down", "down", "primary: not serving"),
             ("serving", "active", "standby: serves traffic")],
            Drawn(payload));
        Assert.Equal("routes to japaneast (failed over)", FrontDoorLine(payload));
    }

    [Fact]
    public void TheRuntimeViewShowsNoRegionServingWhenNoneIsHealthy()
    {
        var payload = Payload(Unreachable, Unhealthy, Unreachable, Unreachable);

        Assert.All(Drawn(payload), drawn => Assert.Equal(("down", "down"), (drawn.Region, drawn.Line)));
        Assert.Equal("no healthy origin", FrontDoorLine(payload));
    }

    [Fact]
    public void TheDeployablePointsAtEveryTileExpectedToServe()
    {
        var ui = new DeployableStatus(TopologyParser.Parse(Topology).Topology!.Environments[0].Deployables[0]);
        ui.FrontDoor!.Record(Answer(Healthy));
        HealthState[] states = [Unreachable, Healthy, Healthy, Healthy];
        foreach (var (state, index) in states.Select((state, index) => (state, index)))
        {
            ui.Nodes[index].Record(Answer(state));
        }

        var assessment = ui.AssessServing(out var serving);

        Assert.Equal("Front Door rotates over 2 of 3 regions: westus3 is not healthy", assessment.Headline);
        Assert.Equal(["prod-east", "prod-north"], serving.Select(target => target.Name));
        ui.Assess(out var first);
        Assert.Same(ui.Nodes[1], first);
    }

    [Fact]
    public async Task TheMonitorWritesWhoLeftTheRotationAndCountsTheNodesAsBefore()
    {
        var time = new SignallingTimeProvider();
        var down = new HashSet<string>(StringComparer.Ordinal);
        var handler = new StubHandler(request => down.Contains(request.RequestUri!.Host) ? throw new HttpRequestException("offline") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, time, topology: Topology);
        string[] Serving() => [.. monitor.Events.Newest.Where(entry => entry.Kind == EventKind.Serving && entry.At == time.GetUtcNow()).Select(entry => entry.Text)];

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Empty(monitor.Events.Newest);

        time.Advance(TimeSpan.FromSeconds(30));
        down.Add("prod-east.example.net");
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(["eastus2 left the rotation: 2 of 3 regions serve traffic."], Serving());
        Assert.Equal("1 of 5 nodes not healthy", monitor.Summary.Text);

        time.Advance(TimeSpan.FromSeconds(30));
        down.UnionWith(["prod-west.example.net", "prod-north.example.net"]);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(["Failover: the rotation → japaneast. No primary node is healthy; japaneast is expected to serve traffic."], Serving());

        time.Advance(TimeSpan.FromSeconds(30));
        down.Clear();
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(["Failback: japaneast → the rotation. 3 of 3 regions serve traffic."], Serving());
    }
}
