using System.Text.Json;

namespace Dashboard.Tests;

/// <summary>
/// The marks of a detailed health check on a web app's tile, and the dependencies outside the subscription, which take
/// their state from those checks: the sample diagram of uat with one or two dependencies added, as the deployment
/// writes them for a deployable with <c>dependencies</c>.
/// </summary>
public class DependencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("http://localhost:5210/");
    private const string Gateway = "dep_ui_LLM_gateway";
    private const string Payments = "dep_ui_Payment_API";

    private static readonly Topology SampleTopology =
        TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;

    private static readonly string[] WebApps = ["app_ui_primary", "app_ui_standby"];

    private static readonly RuntimeManifest Sample = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;

    /// <summary>The sample manifest of uat with the dependencies of ui: a node each, and a relationship from each web app.</summary>
    private static RuntimeManifest Manifest(params (string Alias, string Name, string? HealthCheck)[] dependencies) => Sample with
    {
        Nodes = [.. Sample.Nodes, .. dependencies.Select(dependency => new RuntimeNode(dependency.Alias, RuntimeNodeKind.Dependency, dependency.Name, Deployable: "ui", HealthCheck: dependency.HealthCheck, DependencyKind: "external"))],
        Edges =
        [
            .. Sample.Edges,
            .. from dependency in dependencies
               from app in WebApps
               select new RuntimeEdge($"{dependency.Alias}-to-{app}", app, dependency.Alias, RuntimeEdgeKind.Dependency),
        ],
    };

    private static readonly RuntimeManifest OneDependency = Manifest((Gateway, "LLM gateway", "LlmGateway"));

    private static ProbeResult Answer(int status, ProbeKind probe = ProbeKind.Health) =>
        new(HealthClassifier.FromStatusCode(status), status, 40, Now, "2.4.21", Probe: probe);

    private static TelemetrySnapshot Telemetry(int http) => new(12, 10, 2, 0, 85, 4, 6, 30, 12, http, Now);

    /// <summary>
    /// uat with both web apps checked: their status, and what each one's detailed health check says about the gateway
    /// (null: it reports no entries at all).
    /// </summary>
    private static EnvironmentStatus Uat(string? primary = "Healthy", string? standby = "Healthy", int primaryStatus = 200, int standbyStatus = 200, ProbeKind probe = ProbeKind.Health)
    {
        var uat = new EnvironmentStatus(SampleTopology.Environments.Single(environment => environment.Name == "uat"));
        var ui = uat.Deployables[0];
        ui.FrontDoor!.Record(Answer(200, probe));
        ui.Nodes[0].Record(Answer(primaryStatus, probe));
        ui.Nodes[1].Record(Answer(standbyStatus, probe));
        ui.Nodes[0].RecordHealthDetail(primary is null ? null : HealthDetailTests.Detail(("API", "Healthy"), ("LlmGateway", primary)));
        ui.Nodes[1].RecordHealthDetail(standby is null ? null : HealthDetailTests.Detail(("API", "Healthy"), ("LlmGateway", standby)));
        return uat;
    }

    private static RuntimePayload Build(EnvironmentStatus? environment, RuntimeManifest? manifest = null) =>
        RuntimePayloadBuilder.Build(manifest ?? OneDependency, environment, Page, TimeZoneInfo.Utc);

    private static RuntimeTile Tile(RuntimePayload payload, string alias) => payload.Nodes.Single(tile => tile.Alias == alias);

    private static RuntimeEdgeMark Edge(RuntimePayload payload, string id) => payload.Edges.Single(edge => edge.Id == id);

    [Fact]
    public void TheManifestNamesADependencyItsHealthCheckAndItsRelationships()
    {
        const string Json = """
            { "environment": "uat",
              "nodes": [
                { "alias": "app_ui_primary", "qualifiedName": "sub.rg_tier.region_primary.plan_primary.app_ui_primary", "kind": "webapp", "name": "app-demo-uat-ui", "url": "https://uat-west.example.net" },
                { "alias": "dep_ui_LLM_gateway", "qualifiedName": "dep_ui_LLM_gateway", "kind": "dependency", "deployable": "ui", "name": "LLM gateway",
                  "healthCheck": "LlmGateway", "dependencyKind": "external", "url": null },
                { "alias": "dep_ui_Mail", "kind": "dependency", "name": "Mail", "healthCheck": null } ],
              "edges": [ { "id": "dep_ui_LLM_gateway-to-app_ui_primary", "from": "app_ui_primary", "to": "dep_ui_LLM_gateway", "kind": "dependency" } ] }
            """;

        var manifest = RuntimeManifestParser.ParseManifest(Json).Value!;

        Assert.Equal(
            new RuntimeNode("dep_ui_LLM_gateway", RuntimeNodeKind.Dependency, "LLM gateway", null, "ui", HealthCheck: "LlmGateway", DependencyKind: "external"),
            manifest.Nodes[1]);
        Assert.Equal(new RuntimeNode("dep_ui_Mail", RuntimeNodeKind.Dependency, "Mail"), manifest.Nodes[2]);
        Assert.Null(manifest.Nodes[0].HealthCheck);
        Assert.Equal(new RuntimeEdge("dep_ui_LLM_gateway-to-app_ui_primary", "app_ui_primary", "dep_ui_LLM_gateway", RuntimeEdgeKind.Dependency), Assert.Single(manifest.Edges));
    }

    [Fact]
    public void AManifestWithoutDependenciesGivesThePayloadItGaveBefore()
    {
        var uat = Uat(primary: null, standby: null);

        var payload = RuntimePayloadBuilder.Build(Sample, uat, Page, TimeZoneInfo.Utc);

        Assert.DoesNotContain(payload.Nodes, tile => tile.Alias.StartsWith("dep_", StringComparison.Ordinal));
        Assert.All(payload.Nodes.SelectMany(tile => tile.Lines), line => Assert.Null(line.Marks));
        Assert.DoesNotContain("\"marks\"", payload.ToJson(), StringComparison.Ordinal);
        Assert.Equal(new RuntimeTileLine("primary: serves traffic", "serving"), Tile(payload, "app_ui_primary").Lines[^1]);
    }

    [Fact]
    public void AWebAppsTileEndsWithOneMarkPerEntryOfItsHealthCheck()
    {
        var uat = Uat();
        uat.Deployables[0].Nodes[0].RecordHealthDetail(HealthDetail.Parse(HealthDetailTests.Answer, Now));

        var payload = Build(uat, Sample);

        var primary = Tile(payload, "app_ui_primary").Lines;
        Assert.Equal(new RuntimeTileLine("primary: serves traffic", "serving"), primary[^2]);
        Assert.Equal(("2 of 5 checks not healthy", "warn"), (primary[^1].Text, primary[^1].Tone));
        Assert.Equal(
            [
                new RuntimeCheckMark("healthy", "API: Healthy. API layer is healthy. Took under 0.1 ms."),
                new RuntimeCheckMark("failed", "DataAccess: Unhealthy. Database connection failed: login timeout expired. Took 15 s."),
                new RuntimeCheckMark("healthy", "Jeffrey: Healthy. Took under 0.1 ms."),
                new RuntimeCheckMark("degraded", "LlmGateway: Degraded. Chat client answered slowly: 4.2 s (threshold 2 s). Took 44.1 ms."),
                new RuntimeCheckMark("healthy", "self: Healthy. Took under 0.1 ms."),
            ],
            primary[^1].Marks!);

        var standby = Tile(payload, "app_ui_standby").Lines[^1];
        Assert.Equal(("2 checks healthy", "plain"), (standby.Text, standby.Tone));
        Assert.Equal(["healthy", "healthy"], standby.Marks!.Select(mark => mark.State));

        // The Front Door endpoint answers for one node: it has no marks.
        Assert.All(Tile(payload, "fd_ui").Lines, line => Assert.Null(line.Marks));
    }

    [Fact]
    public void TheMarksTravelInThePayloadAsStateAndTitle()
    {
        using var document = JsonDocument.Parse(Build(Uat(primary: "Degraded"), Sample).ToJson());

        var line = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(tile => tile.GetProperty("alias").GetString() == "app_ui_primary")
            .GetProperty("lines").EnumerateArray().Last();

        Assert.Equal("LlmGateway degraded", line.GetProperty("text").GetString());
        Assert.Equal(
            ["healthy|API: Healthy. API says healthy. Took 1.5 ms.", "degraded|LlmGateway: Degraded. LlmGateway says degraded. Took 1.5 ms."],
            line.GetProperty("marks").EnumerateArray().Select(mark => $"{mark.GetProperty("state").GetString()}|{mark.GetProperty("title").GetString()}"));
    }

    [Fact]
    public void ALineHoldsEightMarksAndTheEntriesThatAreNotHealthyComeFirst()
    {
        var uat = Uat();
        var names = Enumerable.Range(1, 11).Select(index => ($"check{index}", index is 10 or 11 ? "Unhealthy" : "Healthy")).ToArray();
        uat.Deployables[0].Nodes[0].RecordHealthDetail(HealthDetailTests.Detail(names));

        var line = Tile(Build(uat, Sample), "app_ui_primary").Lines[^1];

        Assert.Equal("2 of 11 checks not healthy", line.Text);
        Assert.Equal(RuntimePayloadBuilder.MostMarks, line.Marks!.Count);
        Assert.Equal(["failed", "failed", "healthy"], line.Marks.Take(3).Select(mark => mark.State));
        Assert.StartsWith("check10: Unhealthy.", line.Marks[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnhealthyWebAppSaysInItsTooltipWhichEntriesFailed()
    {
        var uat = Uat(primaryStatus: 503);
        uat.Deployables[0].Nodes[0].RecordHealthDetail(HealthDetail.Parse(HealthDetailTests.Answer, Now));

        var primary = Tile(Build(uat, Sample), "app_ui_primary");

        Assert.Equal("unhealthy", primary.State);
        Assert.EndsWith(
            "\nNot healthy: DataAccess (Unhealthy: Database connection failed: login timeout expired), LlmGateway (Degraded: Chat client answered slowly: 4.2 s (threshold 2 s))",
            primary.Title,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Not healthy", Tile(Build(Uat(), Sample), "app_ui_primary").Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ADependencyIsReachableWhenItsEntryIsHealthyOnAWebAppThatPasses()
    {
        var payload = Build(Uat());

        var gateway = Tile(payload, Gateway);
        Assert.Equal(("healthy", "Reachable"), (gateway.State, gateway.Label));
        Assert.Equal(new RuntimeTileLine("LlmGateway says healthy", "ok"), Assert.Single(gateway.Lines));
        Assert.Null(gateway.History);
        Assert.Null(gateway.Facts);
        Assert.Equal(
            "LLM gateway: reachable, by the health check of app-cmdemo2-uat-ui, app-cmdemo2-uat-ui-eastus2 (last 22:00:00). The browser does not call it. LlmGateway: Healthy. LlmGateway says healthy. Took 1.5 ms.",
            gateway.Title);
        Assert.Equal(["fd_ui", "app_ui_standby", "app_ui_primary", "sqldb", "swa_dashboard", Gateway], payload.Nodes.Select(tile => tile.Alias));
    }

    [Fact]
    public void ALongDescriptionIsCutForTheLineAndWholeInTheTooltip()
    {
        const string Words = "Chat client is not configured. Set the following environment variables: AI_OpenAI_ApiKey, AI_OpenAI_Url";
        var uat = Uat();
        foreach (var node in uat.Deployables[0].Nodes)
        {
            node.RecordHealthDetail(new HealthDetail([new HealthCheckEntry("LlmGateway", CheckState.Healthy, Words, 19.1)], Now));
        }

        var gateway = Tile(Build(uat), Gateway);

        Assert.Equal(new RuntimeTileLine("Chat client is not configured", "ok"), Assert.Single(gateway.Lines));
        Assert.Contains(Words, gateway.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithoutWordsOfItsOwnGetsThePagesWords()
    {
        var uat = Uat();
        foreach (var node in uat.Deployables[0].Nodes)
        {
            node.RecordHealthDetail(new HealthDetail([new HealthCheckEntry("LlmGateway", CheckState.Healthy)], Now));
        }

        Assert.Equal(new RuntimeTileLine("health check LlmGateway passed", "ok"), Assert.Single(Tile(Build(uat), Gateway).Lines));
    }

    [Theory]
    [InlineData("Degraded", "Degraded")]
    [InlineData("Unhealthy", "Unhealthy")]
    public void ADependencyNoWebAppReachesIsWhatItsEntrySays(string status, string label)
    {
        var gateway = Tile(Build(Uat(primary: status, standby: status)), Gateway);

        Assert.Equal(("unhealthy", label), (gateway.State, gateway.Label));
        Assert.Equal(new RuntimeTileLine($"LlmGateway says {status.ToLowerInvariant()}", "warn"), Assert.Single(gateway.Lines));
        Assert.StartsWith("LLM gateway: by the health check of app-cmdemo2-uat-ui (last 22:00:00). The browser does not call it. LlmGateway: ", gateway.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void OneWebAppThatReachesItIsEnoughAndTheLineNamesTheOneThatDoesNot()
    {
        var gateway = Tile(Build(Uat(primary: "Degraded")), Gateway);

        Assert.Equal(("healthy", "Reachable"), (gateway.State, gateway.Label));
        Assert.Equal(new RuntimeTileLine("westus3 reports it degraded", "warn"), Assert.Single(gateway.Lines));
        Assert.Contains("by the health check of app-cmdemo2-uat-ui-eastus2 (last", gateway.Title, StringComparison.Ordinal);
        Assert.EndsWith("Not so for app-cmdemo2-uat-ui: LlmGateway: Degraded. LlmGateway says degraded. Took 1.5 ms.", gateway.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AHealthyEntryOnAWebAppThatFailsItsHealthCheckIsNotConfirmed()
    {
        var gateway = Tile(Build(Uat(standby: null, primaryStatus: 503)), Gateway);

        Assert.Equal(("neutral", "Not confirmed"), (gateway.State, gateway.Label));
        Assert.Equal(new RuntimeTileLine("no web app that reports it passes", "muted"), Assert.Single(gateway.Lines));
    }

    [Fact]
    public void WithoutAnAnswerOfTheDetailedHealthCheckItsStateIsNotKnown()
    {
        var silent = Tile(Build(Uat(primary: null, standby: null)), Gateway);
        var unnamed = Tile(Build(Uat(), Manifest((Gateway, "LLM gateway", "Llm"))), Gateway);

        Assert.Equal(("neutral", "Not known"), (silent.State, silent.Label));
        Assert.Equal(new RuntimeTileLine("no detailed health check answers", "muted"), Assert.Single(silent.Lines));
        Assert.Equal(("neutral", "Not known"), (unnamed.State, unnamed.Label));
        Assert.Equal(new RuntimeTileLine("no health check entry Llm", "muted"), Assert.Single(unnamed.Lines));
        Assert.Contains("has no entry named Llm", unnamed.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ADependencyWithoutANamedHealthCheckOrWithTheLivenessProbeIsNotProbed()
    {
        var unnamed = Tile(Build(Uat(), Manifest((Gateway, "LLM gateway", null))), Gateway);
        var liveness = Tile(Build(Uat(primary: null, standby: null, probe: ProbeKind.Liveness)), Gateway);
        var unused = Tile(Build(Uat(), OneDependency with { Edges = Sample.Edges }), Gateway);

        Assert.Equal(("neutral", "Not probed", "no health check names it"), (unnamed.State, unnamed.Label, unnamed.Lines[0].Text));
        Assert.Equal(("neutral", "Not probed", "probe Liveness leaves it alone"), (liveness.State, liveness.Label, liveness.Lines[0].Text));
        Assert.Equal(("neutral", "Not probed", "no web app that uses it is checked"), (unused.State, unused.Label, unused.Lines[0].Text));
    }

    [Fact]
    public void BeforeTheFirstCheckADependencyWaitsAndWithoutTheTopologyItIsNotProbed()
    {
        var waiting = Tile(Build(new EnvironmentStatus(SampleTopology.Environments.Single(environment => environment.Name == "uat"))), Gateway);
        var unknown = Tile(Build(null), Gateway);

        Assert.Equal(("checking", "Checking"), (waiting.State, waiting.Label));
        Assert.Equal(new RuntimeTileLine("waiting for the health checks", "muted"), Assert.Single(waiting.Lines));
        Assert.Equal(("neutral", "Not probed"), (unknown.State, unknown.Label));
    }

    [Fact]
    public void TheArrowToAnOnlyDependencyCarriesTheWebAppsOutgoingHttpCalls()
    {
        var uat = Uat();
        uat.Deployables[0].Nodes[0].RecordTelemetry(Telemetry(http: 7));

        var payload = Build(uat);

        var primary = Edge(payload, $"{Gateway}-to-app_ui_primary");
        Assert.Equal(("active", "7", "calls/min", "outgoing HTTP calls"), (primary.State, primary.Number, primary.Unit, primary.Text));
        Assert.Equal(
            "app_ui_primary to LLM gateway. Carries the traffic. Last minute, counted by the web app: 7 outgoing HTTP calls, all of them (it has this one dependency).",
            primary.Title);
        Assert.Null(primary.Link);

        // The standby reports no numbers: a dash, and the line is idle.
        var standby = Edge(payload, $"{Gateway}-to-app_ui_standby");
        Assert.Equal(("idle", "–", "outgoing HTTP calls"), (standby.State, standby.Number, standby.Text));
        Assert.EndsWith("Calls per minute: the web app reports none.", standby.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrendOfTheArrowFollowsTheOutgoingCalls()
    {
        var uat = Uat();
        foreach (var http in new[] { 2, 8, 4 })
        {
            uat.Deployables[0].Nodes[0].RecordTelemetry(Telemetry(http));
        }

        var trend = Edge(Build(uat), $"{Gateway}-to-app_ui_primary").Trend!;

        Assert.Equal([0.25, 1, 0.5], trend.Points);
        Assert.Equal("Outgoing HTTP calls per minute, last 3 checks: 2 to 8, now 4", trend.Title);
    }

    [Fact]
    public void WithMoreThanOneDependencyTheArrowsShowADash()
    {
        var uat = Uat();
        uat.Deployables[0].Nodes[0].RecordTelemetry(Telemetry(http: 7));

        var payload = Build(uat, Manifest((Gateway, "LLM gateway", "LlmGateway"), (Payments, "Payment API", "Payments")));

        Assert.All(new[] { Gateway, Payments }, alias =>
        {
            var edge = Edge(payload, $"{alias}-to-app_ui_primary");
            Assert.Equal(("active", "–", "not counted apart"), (edge.State, edge.Number, edge.Text));
            Assert.Null(edge.Trend);
            Assert.EndsWith("the web app counts its outgoing HTTP calls as one number, and it has more than one dependency.", edge.Title, StringComparison.Ordinal);
        });
        Assert.Equal("Not known", Tile(payload, Payments).Label);
        Assert.Equal("Reachable", Tile(payload, Gateway).Label);
    }

    [Fact]
    public void AWebAppThatIsDownCallsNothingAndItsArrowIsIdle()
    {
        var payload = Build(Uat(primary: null, primaryStatus: 503));

        Assert.Equal("idle", Edge(payload, $"{Gateway}-to-app_ui_primary").State);
        Assert.Equal("active", Edge(payload, $"{Gateway}-to-app_ui_standby").State);
        Assert.Equal("Reachable", Tile(payload, Gateway).Label);
    }
}
