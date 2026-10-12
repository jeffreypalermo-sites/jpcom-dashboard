using System.Text.Json;

namespace Dashboard.Tests;

public class RuntimePayloadBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("http://localhost:5210/");

    private static readonly Topology SampleTopology =
        TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;

    private static RuntimeManifest Manifest(string environment) =>
        RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample($"{environment}.json")).Value!;

    private static EnvironmentStatus Environment(string name) =>
        new(SampleTopology.Environments.Single(environment => environment.Name == name));

    private static ProbeResult Answer(int status, string version = "2.4.21") =>
        new(HealthClassifier.FromStatusCode(status), status, 40, Now, version);

    private static readonly string[] CheckedAliases = ["fd_ui", "app_ui_primary", "app_ui_standby"];

    private static readonly ProbeResult NoAnswer = new(HealthState.Unreachable, null, null, Now, null, "No answer within 10 s.");

    /// <summary>uat with every endpoint answering: Front Door, primary westus3, standby eastus2.</summary>
    private static EnvironmentStatus Uat(int frontDoor = 200, int primary = 200, int standby = 200, string pinned = "2.4.21")
    {
        var uat = Environment("uat");
        var ui = uat.Deployables[0];
        ui.FrontDoor!.Record(Answer(frontDoor));
        ui.Nodes[0].Record(Answer(primary));
        ui.Nodes[1].Record(standby == 0 ? NoAnswer : Answer(standby, "2.4.20"));
        uat.Record(PinnedVersions.Parse($$"""{ "ui": "{{pinned}}" }"""));
        return uat;
    }

    private static RuntimePayload Build(EnvironmentStatus? environment, string manifest = "uat") =>
        RuntimePayloadBuilder.Build(Manifest(manifest), environment, Page, TimeZoneInfo.Utc);

    private static RuntimeTile Tile(RuntimePayload payload, string alias) => payload.Nodes.Single(tile => tile.Alias == alias);

    /// <summary>A tile's lines as words and tone: what a reader sees, whatever is a link.</summary>
    private static (string Text, string Tone)[] Words(RuntimeTile tile) => [.. tile.Lines.Select(line => (line.Text, line.Tone))];

    // The marks below are compared without the link of the region's name, which the sample manifest gives every
    // region (RuntimeBoxLinkTests has the links).
    private static string Region(RuntimePayload payload, string alias) => payload.Regions.Single(region => region.Alias == alias).State;

    private static string Edge(RuntimePayload payload, string id) => payload.Edges.Single(edge => edge.Id == id).State;

    [Fact]
    public void EveryNodeButThePersonGetsATileAndEveryRegionAndRelationshipAMark()
    {
        var payload = Build(Uat());

        Assert.Equal(["fd_ui", "app_ui_standby", "app_ui_primary", "sqldb", "swa_dashboard"], payload.Nodes.Select(tile => tile.Alias));
        Assert.Equal(["region_standby", "region_primary", "region_data"], payload.Regions.Select(region => region.Alias));
        Assert.Equal(Manifest("uat").Edges.Select(edge => edge.Id), payload.Edges.Select(edge => edge.Id));
    }

    [Fact]
    public void BeforeTheFirstCheckEveryCheckedNodeIsBeingChecked()
    {
        var payload = Build(Environment("uat"));

        Assert.All(CheckedAliases, alias =>
        {
            var tile = Tile(payload, alias);
            Assert.Equal("checking", tile.State);
            Assert.Equal("Checking", tile.Label);
            Assert.Equal("not checked yet", tile.Facts);
            Assert.Empty(tile.History!);
        });
        Assert.Equal("checking", Region(payload, "region_primary"));
        Assert.Equal("checking", Edge(payload, "fd_ui-to-app_ui_primary"));
        Assert.Equal("neutral", Region(payload, "region_data"));
    }

    [Fact]
    public void WithEveryNodeHealthyThePrimaryRegionServesAndTheStandbyWaits()
    {
        var payload = Build(Uat());

        var primary = Tile(payload, "app_ui_primary");
        Assert.Equal("healthy", primary.State);
        Assert.Equal("Healthy", primary.Label);
        Assert.Equal("HTTP 200 · 40 ms", primary.Facts);
        Assert.Equal([("version 2.4.21", "strong"), ("pinned 2.4.21: in sync", "insync"), ("primary: serves traffic", "serving")], Words(primary));
        Assert.Equal(["healthy"], primary.History!);
        Assert.Contains("https://app-cmdemo2-uat-ui.azurewebsites.net/", primary.Title, StringComparison.Ordinal);

        var standby = Tile(payload, "app_ui_standby");
        Assert.Equal([("version 2.4.20", "strong"), ("differs from pinned 2.4.21", "differs"), ("standby: ready, no traffic", "muted")], Words(standby));

        Assert.Equal(new RuntimeRegionMark("region_primary", "serving", "serving traffic"), payload.Regions.Single(region => region.Alias == "region_primary") with { NameLink = null });
        Assert.Equal(new RuntimeRegionMark("region_standby", "standby", "standby: ready"), payload.Regions.Single(region => region.Alias == "region_standby") with { NameLink = null });
        Assert.Equal("active", Edge(payload, "fd_ui-to-app_ui_primary"));
        Assert.Equal("idle", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal("active", Edge(payload, "app_ui_primary-to-sqldb"));
        Assert.Equal("idle", Edge(payload, "app_ui_standby-to-sqldb"));
        Assert.Equal("active", Edge(payload, "browser-to-fd_ui"));

        var frontDoor = Tile(payload, "fd_ui");
        Assert.Equal([("version 2.4.21", "strong"), ("routes to westus3 (priority 1)", "plain"), ("agrees with the web apps", "ok")], Words(frontDoor));
    }

    [Fact]
    public void WhenThePrimaryAnswers503TheDiagramShowsTheFailover()
    {
        var payload = Build(Uat(primary: 503));

        var primary = Tile(payload, "app_ui_primary");
        Assert.Equal("unhealthy", primary.State);
        Assert.Equal("Unhealthy", primary.Label);
        Assert.Equal("HTTP 503 · 40 ms", primary.Facts);
        Assert.Equal(new RuntimeTileLine("primary: not serving", "plain"), primary.Lines[^1]);
        Assert.Equal(new RuntimeTileLine("standby: serves traffic", "serving"), Tile(payload, "app_ui_standby").Lines[^1]);

        Assert.Equal("down", Region(payload, "region_primary"));
        Assert.Equal("serving", Region(payload, "region_standby"));
        Assert.Equal("down", Edge(payload, "fd_ui-to-app_ui_primary"));
        Assert.Equal("active", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal("idle", Edge(payload, "app_ui_primary-to-sqldb"));
        Assert.Equal("active", Edge(payload, "app_ui_standby-to-sqldb"));
        Assert.Equal(new RuntimeTileLine("routes to eastus2 (failed over)", "serving"), Tile(payload, "fd_ui").Lines[1]);
    }

    [Fact]
    public void WhenNothingIsHealthyNoRegionServes()
    {
        var payload = Build(Uat(frontDoor: 503, primary: 503, standby: 0));

        Assert.Equal("unreachable", Tile(payload, "app_ui_standby").State);
        Assert.Equal("no answer", Tile(payload, "app_ui_standby").Facts);
        Assert.Equal("down", Region(payload, "region_primary"));
        Assert.Equal("down", Region(payload, "region_standby"));
        Assert.Equal("down", Edge(payload, "browser-to-fd_ui"));
        Assert.Equal("down", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal(new RuntimeTileLine("no healthy origin", "plain"), Tile(payload, "fd_ui").Lines[1]);
        Assert.Equal(new RuntimeTileLine("agrees with the web apps", "ok"), Tile(payload, "fd_ui").Lines[2]);
    }

    [Fact]
    public void AnEnvironmentWithoutAStandbyHasNoStandbyRegionNorItsRelationships()
    {
        var tdd = Environment("tdd");
        tdd.Deployables[0].Nodes[0].Record(Answer(200));
        tdd.Deployables[0].FrontDoor!.Record(Answer(200));

        var payload = Build(tdd, "tdd");

        Assert.Equal(["fd_ui", "app_ui_primary", "sqldb", "swa_dashboard"], payload.Nodes.Select(tile => tile.Alias));
        Assert.Equal(["region_primary", "region_data"], payload.Regions.Select(region => region.Alias));
        Assert.DoesNotContain(payload.Edges, edge => edge.Id.Contains("standby", StringComparison.Ordinal));
        Assert.Equal("serving", Region(payload, "region_primary"));
        Assert.Equal("active", Edge(payload, "fd_ui-to-app_ui_primary"));
    }

    [Fact]
    public void TheDatabaseWaitsForTheHealthChecksAndTheDashboardKnowsItsOwnSite()
    {
        var tdd = Build(Environment("tdd"), "tdd");
        var uat = Build(Environment("uat"));

        Assert.Equal(("checking", "Checking"), (Tile(tdd, "sqldb").State, Tile(tdd, "sqldb").Label));
        Assert.Equal(new RuntimeTileLine("waiting for the health checks", "muted"), Assert.Single(Tile(tdd, "sqldb").Lines));
        Assert.Null(Tile(tdd, "sqldb").History);
        // The sample's tdd dashboard is http://localhost:5210, the page of dotnet run; uat's address is not known.
        Assert.Equal("This page", Tile(tdd, "swa_dashboard").Label);
        Assert.Equal(new RuntimeTileLine("its address is not in this deployment", "muted"), Assert.Single(Tile(uat, "swa_dashboard").Lines));
        Assert.Equal("neutral", Edge(uat, "browser-to-swa_dashboard"));
        Assert.Equal(new RuntimeRegionMark("region_data", "neutral", "database, static sites: not probed"), uat.Regions.Single(region => region.Alias == "region_data") with { NameLink = null });
    }

    [Fact]
    public void APassingHealthCheckOfAWebAppSaysTheDatabaseIsReachable()
    {
        var payload = Build(Uat(standby: 0));

        var database = Tile(payload, "sqldb");
        Assert.Equal(("healthy", "Reachable"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("health check of westus3 passed", "ok"), Assert.Single(database.Lines));
        Assert.Null(database.Facts);
        Assert.Contains("app-cmdemo2-uat-ui connected to it (last 22:00", database.Title, StringComparison.Ordinal);
        Assert.Equal(new RuntimeRegionMark("region_data", "neutral", "database: reachable; static sites: not probed"), payload.Regions.Single(region => region.Alias == "region_data") with { NameLink = null });
        Assert.Equal(new RuntimeTileLine("health checks of 2 web apps passed", "ok"), Assert.Single(Tile(Build(Uat()), "sqldb").Lines));
    }

    [Fact]
    public void FailingHealthChecksLeaveTheDatabaseNotConfirmedNotDown()
    {
        var payload = Build(Uat(frontDoor: 503, primary: 503, standby: 0));

        var database = Tile(payload, "sqldb");
        Assert.Equal(("neutral", "Not confirmed"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("no health check of its apps passes", "muted"), Assert.Single(database.Lines));
        Assert.Equal("database, static sites: not probed", payload.Regions.Single(region => region.Alias == "region_data").Label);
    }

    [Fact]
    public void TheLivenessProbeLeavesTheDatabaseNotProbed()
    {
        var uat = Environment("uat");
        foreach (var node in uat.Deployables[0].Nodes)
        {
            node.Record(Answer(200) with { Probe = ProbeKind.Liveness });
        }

        var database = Tile(Build(uat), "sqldb");

        Assert.Equal(("neutral", "Not probed"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("probe Liveness leaves it alone", "muted"), Assert.Single(database.Lines));
    }

    /// <summary>uat with a container app, as the deployment draws one: kind containerapp, its deployable and its address.</summary>
    private static RuntimeManifest WithContainerApp()
    {
        var manifest = Manifest("uat");
        var app = new RuntimeNode(
            "ca_scorecard",
            RuntimeNodeKind.ContainerApp,
            "ca-demo-uat-scorecard",
            new Uri("https://ca-demo-uat-scorecard.graybush-1.southcentralus.azurecontainerapps.io"),
            "scorecard");
        return manifest with { Nodes = [.. manifest.Nodes, app] };
    }

    private const string ContainerHost = "ca-demo-uat-scorecard.graybush-1.southcentralus.azurecontainerapps.io";

    /// <summary>The delivery facts with what the hourly health reports found for the container apps of uat.</summary>
    private static DeliveryReport Delivery(string containerApps) =>
        DeliveryReport.Parse($$"""
            { "generated": "2026-10-04T21:37:00Z",
              "environments": [ { "name": "uat", "deployables": [],
                "health": { "last24Hours": { "reports": 24, "healthy": 23 }, "last7Days": { "reports": 168, "healthy": 160 }, "lastFailure": "2026-10-04T09:02:00Z",
                            "containerApps": {{containerApps}} } } ] }
            """)!;

    private static RuntimeTile ContainerTile(DeliveryReport? delivery, DateTimeOffset? now, ContainerAppStatus? probed = null) =>
        Tile(RuntimePayloadBuilder.Build(WithContainerApp(), Uat(), Page, TimeZoneInfo.Utc, delivery: delivery, now: now, containerApps: _ => probed), "ca_scorecard");

    private static string Reported(string result, DateTimeOffset at) =>
        $$"""[ { "name": "scorecard", "last": { "result": "{{result}}", "at": "{{TimeText.Iso(at)}}" }, "last24Hours": { "reports": 24, "healthy": 23 }, "last7Days": { "reports": 168, "healthy": 160 } } ]""";

    private static ContainerAppStatus Probed(ContainerAppState state, int? status = null, bool opaque = false) =>
        new(state, new ContainerAppAnswer(opaque || status is null ? "/" : "/health", status is not null, status, opaque, status is null && !opaque ? null : 85, Now.AddSeconds(-20), status is null && !opaque ? "No answer within 10 s." : null), opaque);

    [Fact]
    public void AContainerAppIsBeingProbedUntilThePageHasAskedIt()
    {
        var tile = ContainerTile(null, Now);

        Assert.Equal(("checking", "Probing", "not probed yet"), (tile.State, tile.Label, tile.Facts));
        Assert.Equal(new RuntimeTileLine(ContainerHost, "muted"), Assert.Single(tile.Lines));
        Assert.Null(tile.History);
        Assert.Contains("This page asks it once a minute while it shows this diagram", tile.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContainerAppState.Waking, null, false, "checking", "Waking", "no answer yet")]
    [InlineData(ContainerAppState.Responding, null, true, "healthy", "Responding", "answered · 85 ms")]
    [InlineData(ContainerAppState.Healthy, 200, false, "healthy", "Healthy", "HTTP 200 · 85 ms")]
    [InlineData(ContainerAppState.Unhealthy, 503, false, "unhealthy", "Unhealthy", "HTTP 503 · 85 ms")]
    [InlineData(ContainerAppState.NotResponding, null, false, "unreachable", "Not responding", "no answer")]
    public void AContainerAppTakesTheStateOfThePagesProbeInTheColoursOfTheOtherStates(ContainerAppState state, int? status, bool opaque, string drawn, string label, string facts)
    {
        var tile = ContainerTile(null, Now, Probed(state, status, opaque));

        Assert.Equal((drawn, label, facts), (tile.State, tile.Label, tile.Facts));
        Assert.Contains($"ca-demo-uat-scorecard ({ContainerHost}): {label}.", tile.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAppThatDoesNotAllowThePagesOriginRespondsAndTheTooltipSaysWhatThePageCannotSee()
    {
        var tile = ContainerTile(null, Now, Probed(ContainerAppState.Responding, opaque: true));

        Assert.Contains("does not allow this page's origin (CORS), so the page cannot read its answer or its health", tile.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AContainerAppSaysWhatTheHourlyHealthReportFoundInNeutralWordsUnderItsProbe()
    {
        var tile = ContainerTile(Delivery(Reported("healthy", Now.AddMinutes(-40))), Now, Probed(ContainerAppState.Healthy, 200));

        Assert.Equal(("healthy", "Healthy", "HTTP 200 · 85 ms"), (tile.State, tile.Label, tile.Facts));
        Assert.Equal(
            [(ContainerHost, "muted"), ("hourly: healthy, 40 min ago", "plain"), ("24 h: 23 of 24 · 7 d: 160 of 168", "muted")],
            Words(tile));
        Assert.Null(tile.History);
        Assert.Contains("The last report that asked it ended 2026-10-04 21:20 +00:00 (40 min ago) and found it healthy.", tile.Title, StringComparison.Ordinal);
        Assert.Contains("Healthy in 23 of the 24 reports of 24 hours that asked it. Healthy in 160 of the 168 reports of 7 days that asked it.", tile.Title, StringComparison.Ordinal);
        Assert.EndsWith(AvailabilityText.Note, tile.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unhealthy", -90, "hourly: unhealthy, 90 min ago")]
    [InlineData("unreachable", -119, "hourly: unreachable, 119 min ago")]
    [InlineData("unreachable", -120, "hourly: unreachable, 2 h ago")]
    public void AnAppTheReportFoundNotHealthyIsSaidInWordsAndTheBoxTakesTheProbesState(string result, int minutes, string line)
    {
        var tile = ContainerTile(Delivery(Reported(result, Now.AddMinutes(minutes))), Now, Probed(ContainerAppState.Healthy, 200));

        Assert.Equal("healthy", tile.State);
        Assert.Equal((line, "plain"), Words(tile)[1]);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("unhealthy")]
    [InlineData("unreachable")]
    public void AResultOlderThanTwoHoursIsNoRecentCheckAndNeverRed(string result)
    {
        var at = Now - HourlyCheckText.Recent - TimeSpan.FromSeconds(1);

        var tile = ContainerTile(Delivery(Reported(result, at)), Now);

        Assert.Equal("checking", tile.State);
        Assert.Equal(("no recent hourly check", "muted"), Words(tile)[1]);
        Assert.Equal(("24 h: 23 of 24 · 7 d: 160 of 168", "muted"), Words(tile)[2]);
        Assert.Contains($"found it {result}: none in the last two hours, so the box says no recent hourly check.", tile.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoHoursAreCountedFromNowNotFromTheFile()
    {
        var delivery = Delivery(Reported("healthy", Now.AddMinutes(-100)));

        Assert.Equal("hourly: healthy, 100 min ago", Words(ContainerTile(delivery, Now))[1].Text);
        Assert.Equal("no recent hourly check", Words(ContainerTile(delivery, Now.AddMinutes(21)))[1].Text);
        Assert.Null(HourlyCheckText.RecentResult(delivery.ContainerAppsOf("uat")![0], Now.AddMinutes(21)));
        Assert.NotNull(HourlyCheckText.RecentResult(delivery.ContainerAppsOf("uat")![0], Now.AddMinutes(20)));
    }

    [Fact]
    public void AnAppNoReportAskedOrTheFileDoesNotNameHasNoRecentCheck()
    {
        var never = ContainerTile(Delivery("""[ { "name": "scorecard", "last": null, "last24Hours": { "reports": 0, "healthy": 0 }, "last7Days": { "reports": 0, "healthy": 0 } } ]"""), Now);
        var unnamed = ContainerTile(Delivery("""[ { "name": "ui", "last": null, "last24Hours": null, "last7Days": null } ]"""), Now);

        Assert.Equal([(ContainerHost, "muted"), ("no recent hourly check", "muted"), ("24 h: none · 7 d: none", "muted")], Words(never));
        Assert.Contains("No report of the last 7 days asked it.", never.Title, StringComparison.Ordinal);
        Assert.Equal([(ContainerHost, "muted"), ("no recent hourly check", "muted")], Words(unnamed));
        Assert.Contains("The delivery facts name no report of it yet.", unnamed.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountTheFileDoesNotHaveIsLeftOut()
    {
        var tile = ContainerTile(Delivery("""[ { "name": "scorecard", "last": { "result": "healthy", "at": "2026-10-04T21:30:00Z" }, "last24Hours": { "reports": 24, "healthy": 24 }, "last7Days": null } ]"""), Now);

        Assert.Equal(("24 h: 24 of 24", "muted"), Words(tile)[2]);
        Assert.DoesNotContain("7 days that asked it", tile.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheReportsOfContainerAppsTheBoxIsAsBefore()
    {
        // An older file, a system whose logs could not be read, and an update without the time: the address alone.
        var older = DeliveryReport.Parse("""{ "environments": [ { "name": "uat", "health": { "last24Hours": { "reports": 24, "healthy": 24 }, "last7Days": null } } ] }""");

        Assert.Equal([(ContainerHost, "muted")], Words(ContainerTile(older, Now)));
        Assert.Equal([(ContainerHost, "muted")], Words(ContainerTile(Delivery("null"), Now)));
        Assert.Equal([(ContainerHost, "muted")], Words(ContainerTile(Delivery(Reported("healthy", Now)), null)));
    }

    [Fact]
    public void TheHourlyLinesComeBeforeTheActivityLines()
    {
        var activity = new ActivityLine(ActivityKind.Deployed, "1.0.3 reached uat 4 min ago", "demo-scorecard 1.0.3 reached uat 4 min ago");

        var tile = Tile(
            RuntimePayloadBuilder.Build(WithContainerApp(), Uat(), Page, TimeZoneInfo.Utc, [], _ => [activity], Delivery(Reported("healthy", Now.AddMinutes(-5))), Now),
            "ca_scorecard");

        Assert.Equal(
            [(ContainerHost, "muted"), ("hourly: healthy, 5 min ago", "plain"), ("24 h: 23 of 24 · 7 d: 160 of 168", "muted"), ("1.0.3 reached uat 4 min ago", ActivityKind.Deployed)],
            Words(tile));
    }

    [Fact]
    public void AContainerAppTheSystemDoesNotDeclareIsDrawnNotProbedAndTakesNoDeploymentMark()
    {
        var manifest = Manifest("uat");
        var app = new RuntimeNode("cax_1", RuntimeNodeKind.Other, "ca-ai-factory");
        var drawn = manifest with { Nodes = [.. manifest.Nodes, app] };

        var tile = Tile(RuntimePayloadBuilder.Build(drawn, Uat(), Page, TimeZoneInfo.Utc), "cax_1");

        Assert.Equal(("neutral", "Not probed"), (tile.State, tile.Label));
        Assert.Empty(tile.Lines);
        Assert.Equal("ca-ai-factory", tile.Title);
        Assert.DoesNotContain("ca-ai-factory", RuntimePayloadBuilder.Deployables(drawn));
    }

    [Fact]
    public void ADatabaseNoCheckedWebAppUsesIsNotProbed()
    {
        var manifest = Manifest("uat") with { Edges = [.. Manifest("uat").Edges.Where(edge => edge.Kind != RuntimeEdgeKind.Sql)] };

        var database = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc), "sqldb");

        Assert.Equal(("neutral", "Not probed"), (database.State, database.Label));
        Assert.Equal(new RuntimeTileLine("not probed from the browser", "muted"), Assert.Single(database.Lines));
    }

    private const string Group = "/subscriptions/0000/resourceGroups/rg-demo-uat/providers";

    /// <summary>containers.json with what Resource Health said of a vault, an identity and the database, read 40 minutes ago.</summary>
    private static ResourceHealthReport Readings() => ResourceHealthReport.Parse($$"""
        { "generated": "2026-10-04T21:20:00Z", "resources": { "nonprod": [
            { "id": "{{Group}}/Microsoft.KeyVault/vaults/kv-demo", "health": { "status": "available", "source": "resourceHealth", "checkedAt": "2026-10-04T21:20:00Z", "summary": "There aren't any known Azure platform problems affecting this key vault." } },
            { "id": "{{Group}}/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo-uat", "health": { "status": "succeeded", "source": "provisioningState", "checkedAt": "2026-10-04T21:20:00Z" } },
            { "id": "{{Group}}/Microsoft.Sql/servers/sql-demo/databases/sqldb-cmdemo2-uat", "health": { "status": "degraded", "source": "resourceHealth", "checkedAt": "2026-10-04T21:20:00Z" } } ] } }
        """)!;

    private static RuntimeTile ResourceTile(RuntimeNode node, ResourceHealthReport? report, DateTimeOffset? now = null)
    {
        var manifest = Manifest("uat");
        return Tile(RuntimePayloadBuilder.Build(manifest with { Nodes = [.. manifest.Nodes, node] }, Uat(), Page, TimeZoneInfo.Utc, now: now ?? Now, resources: report), node.Alias);
    }

    [Fact]
    public void AResourceBoxSaysWhatResourceHealthSaidInNeutralWordsWithItsAge()
    {
        var vault = new RuntimeNode("res_1", RuntimeNodeKind.Resource, "kv-demo", Ids: [$"{Group}/microsoft.keyvault/vaults/KV-DEMO"]);

        var tile = ResourceTile(vault, Readings());

        Assert.Equal(("neutral", "Available", (string?)null), (tile.State, tile.Label, tile.Facts));
        Assert.Equal([("Resource Health, 40 min ago", "plain")], Words(tile));
        Assert.Contains(ResourceHealthText.Source, tile.Title, StringComparison.Ordinal);
        Assert.Contains("KV-DEMO: available (by Resource Health), read 2026-10-04 21:20 +00:00 (40 min ago). There aren't any known Azure platform problems", tile.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoxOfSeveralResourcesShowsTheMostUrgentWordAndTheCountOfEach()
    {
        var several = new RuntimeNode("res_2", RuntimeNodeKind.Resource, "Resources (3)", Ids:
            [$"{Group}/Microsoft.KeyVault/vaults/kv-demo", $"{Group}/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo-uat", $"{Group}/Microsoft.Storage/storageAccounts/stnew"]);

        var tile = ResourceTile(several, Readings());

        Assert.Equal(("neutral", "Not known"), (tile.State, tile.Label));
        Assert.Equal([("1 not known · 1 provisioned · 1 available, 40 min ago", "plain")], Words(tile));
        Assert.Contains("stnew: not in the hourly reading.", tile.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingOlderThanTwoHoursIsSaidInGreyAndABoxWithoutTheFileSaysSo()
    {
        var vault = new RuntimeNode("res_1", RuntimeNodeKind.Resource, "kv-demo", Ids: [$"{Group}/Microsoft.KeyVault/vaults/kv-demo"]);

        Assert.Equal([("Resource Health, 2 h ago", "muted")], Words(ResourceTile(vault, Readings(), Now.AddHours(2))));
        var unread = ResourceTile(vault, null);
        Assert.Equal(("neutral", "Not known"), (unread.State, unread.Label));
        Assert.Equal([("no Resource Health read yet", "muted")], Words(unread));
    }

    [Fact]
    public void AResourceBoxFromBeforeTheIdsIsAsBefore()
    {
        var tile = ResourceTile(new RuntimeNode("res_1", RuntimeNodeKind.Resource, "kv-demo"), Readings());

        Assert.Equal(("neutral", "Not probed"), (tile.State, tile.Label));
        Assert.Empty(tile.Lines);
    }

    [Fact]
    public void ADatabaseNoCheckedWebAppUsesSaysWhatResourceHealthSaid()
    {
        var uat = Manifest("uat");
        var manifest = uat with
        {
            Edges = [.. uat.Edges.Where(edge => edge.Kind != RuntimeEdgeKind.Sql)],
            Nodes = [.. uat.Nodes.Select(node => node.Alias == "sqldb" ? node with { Ids = [$"{Group}/Microsoft.Sql/servers/sql-demo/databases/sqldb-cmdemo2-uat"] } : node)],
        };

        var database = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc, now: Now, resources: Readings()), "sqldb");

        Assert.Equal(("neutral", "Degraded"), (database.State, database.Label));
        Assert.Equal([("Resource Health, 40 min ago", "plain")], Words(database));
        Assert.StartsWith("sqldb-cmdemo2-uat: the database takes no call from a browser", database.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatabaseAWebAppReachesKeepsItsState()
    {
        var uat = Manifest("uat");
        var manifest = uat with { Nodes = [.. uat.Nodes.Select(node => node.Alias == "sqldb" ? node with { Ids = [$"{Group}/Microsoft.Sql/servers/sql-demo/databases/sqldb-cmdemo2-uat"] } : node)] };

        var database = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc, now: Now, resources: Readings()), "sqldb");

        Assert.Equal(("healthy", "Reachable"), (database.State, database.Label));
    }

    [Fact]
    public void AStaticSiteThisPageDoesNotCheckSaysWhatResourceHealthSaid()
    {
        var uat = Manifest("uat");
        var manifest = uat with { Nodes = [.. uat.Nodes.Select(node => node.Alias == "swa_dashboard" ? node with { Ids = [$"{Group}/Microsoft.KeyVault/vaults/kv-demo"] } : node)] };

        var site = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc, now: Now, resources: Readings()), "swa_dashboard");

        Assert.Equal(("neutral", "Available"), (site.State, site.Label));
        Assert.Contains("the dashboard of this environment. This page does not check it.", site.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AContainerAppWithoutAnAddressSaysWhatResourceHealthSaid()
    {
        var app = new RuntimeNode("cax_1", RuntimeNodeKind.ContainerApp, "ca-internal", Ids: [$"{Group}/Microsoft.KeyVault/vaults/kv-demo"]);

        var tile = ResourceTile(app, Readings());
        var bare = ResourceTile(app with { Ids = null }, Readings());

        Assert.Equal(("neutral", "Available"), (tile.State, tile.Label));
        Assert.Contains("a container app without a public address this page knows", tile.Title, StringComparison.Ordinal);
        Assert.Equal(("neutral", "No address"), (bare.State, bare.Label));
    }

    [Fact]
    public void AnUndeclaredContainerAppIsAskedAtItsRootAndHasNoHourlyLines()
    {
        var app = new RuntimeNode("cax_1", RuntimeNodeKind.ContainerApp, "ca-ai-factory", new Uri("https://ca-ai-factory.happyisland-1.southcentralus.azurecontainerapps.io"), HealthPath: "/health");
        ContainerAppTarget? asked = null;
        var manifest = Manifest("uat");

        var tile = Tile(
            RuntimePayloadBuilder.Build(manifest with { Nodes = [.. manifest.Nodes, app] }, Uat(), Page, TimeZoneInfo.Utc, delivery: Delivery(Reported("healthy", Now)), now: Now, containerApps: target => { asked = target; return null; }),
            "cax_1");

        Assert.Equal(new ContainerAppTarget(app.Url!), asked);
        Assert.Equal([("ca-ai-factory.happyisland-1.southcentralus.azurecontainerapps.io", "muted")], Words(tile));
    }

    [Fact]
    public void ThePageWatchesTheContainerAppsWithAnAddressAndResourceHealthWhereABoxHasIds()
    {
        var declared = WithContainerApp();
        var manifest = declared with
        {
            Nodes =
            [
                .. declared.Nodes.Select(node => node.Alias == "ca_scorecard" ? node with { HealthPath = "/health" } : node),
                new RuntimeNode("cax_1", RuntimeNodeKind.ContainerApp, "ca-internal"),
                new RuntimeNode("res_1", RuntimeNodeKind.Resource, "kv-demo", Ids: ["/x"]),
            ],
        };

        var watch = RuntimePayloadBuilder.Watch(manifest);

        Assert.Equal([new ContainerAppTarget(new Uri($"https://{ContainerHost}"), "/health")], watch.Apps);
        Assert.True(watch.ResourceHealth);
        Assert.False(RuntimePayloadBuilder.Watch(Manifest("uat")).ResourceHealth);
        Assert.Empty(RuntimePayloadBuilder.Watch(Manifest("uat")).Apps);
    }

    [Fact]
    public void ANodeTheTopologyDoesNotHaveIsDrawnNeutralWithTheReason()
    {
        var manifest = Manifest("uat") with
        {
            Nodes =
            [
                .. Manifest("uat").Nodes.Where(node => node.Alias != "app_ui_standby"),
                new RuntimeNode("app_ui_standby", RuntimeNodeKind.WebApp, "app-cmdemo2-uat-ui-westeurope", new Uri("https://app-cmdemo2-uat-ui-westeurope.azurewebsites.net"), "ui", "standby", "westeurope", "region_standby"),
            ],
        };

        var payload = RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc);

        var tile = Tile(payload, "app_ui_standby");
        Assert.Equal(("neutral", "Not checked"), (tile.State, tile.Label));
        Assert.Equal(new RuntimeTileLine("not in topology.json", "muted"), Assert.Single(tile.Lines));
        Assert.Equal(new RuntimeRegionMark("region_standby", "neutral", "not checked"), payload.Regions.Single(region => region.Alias == "region_standby") with { NameLink = null });
        Assert.Equal("neutral", Edge(payload, "fd_ui-to-app_ui_standby"));
        Assert.Equal("healthy", Tile(payload, "app_ui_primary").State);
    }

    [Fact]
    public void AnEnvironmentTheTopologyDoesNotHaveShowsNothingAsChecked()
    {
        var payload = Build(null);

        Assert.All(payload.Nodes, tile => Assert.Equal("neutral", tile.State));
        Assert.All(payload.Regions, region => Assert.Equal("neutral", region.State));
        Assert.All(payload.Edges, edge => Assert.Equal("neutral", edge.State));
    }

    [Fact]
    public void AFrontDoorEndpointWithoutAnAddressIsNotDeployedYet()
    {
        var manifest = Manifest("uat") with
        {
            Nodes = [.. Manifest("uat").Nodes.Select(node => node.Alias == "fd_ui" ? node with { Url = null } : node)],
        };

        var tile = Tile(RuntimePayloadBuilder.Build(manifest, Uat(), Page, TimeZoneInfo.Utc), "fd_ui");

        Assert.Equal(("neutral", "No address"), (tile.State, tile.Label));
        Assert.Equal(new RuntimeTileLine("endpoint not deployed yet", "muted"), Assert.Single(tile.Lines));
    }

    [Fact]
    public void WithoutPinnedVersionsTheTileHasNoComparisonLine()
    {
        var topology = SampleTopology.Environments.Single(environment => environment.Name == "uat") with { VersionsUrl = null };
        var uat = new EnvironmentStatus(topology);
        uat.Deployables[0].Nodes[0].Record(Answer(200));

        var lines = Tile(Build(uat), "app_ui_primary").Lines;

        Assert.Equal(["version 2.4.21", "primary: serves traffic"], lines.Select(line => line.Text));
    }

    [Theory]
    [InlineData(PinnedVersionsState.Pending, "reading the pinned version")]
    [InlineData(PinnedVersionsState.Missing, "no pinned version")]
    [InlineData(PinnedVersionsState.Unavailable, "pinned version not known")]
    public void APinnedVersionThatIsNotKnownSaysWhy(PinnedVersionsState state, string text)
    {
        var uat = Environment("uat");
        uat.Deployables[0].Nodes[0].Record(Answer(200));
        uat.Record(state switch
        {
            PinnedVersionsState.Missing => PinnedVersions.Missing,
            PinnedVersionsState.Unavailable => PinnedVersions.Unavailable("HTTP 500"),
            _ => PinnedVersions.Pending,
        });

        Assert.Equal(new RuntimeTileLine(text, "unknown"), Tile(Build(uat), "app_ui_primary").Lines[1]);
    }

    [Fact]
    public void WithoutTelemetryTheNumberLinesShowADash()
    {
        var payload = Build(Uat());

        var origin = payload.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_standby");
        Assert.Equal((RuntimePayloadBuilder.NoNumber, "calls/min", "when priority 1 is down"), (origin.Number, origin.Unit, origin.Text));
        Assert.Equal("first, while healthy", payload.Edges.Single(edge => edge.Id == "fd_ui-to-app_ui_primary").Text);
        Assert.Equal("app queries", payload.Edges.Single(edge => edge.Id == "app_ui_primary-to-sqldb").Text);
        Assert.Equal((RuntimePayloadBuilder.NoNumber, null), (payload.Edges.Single(edge => edge.Id == "browser-to-fd_ui").Number, payload.Edges.Single(edge => edge.Id == "browser-to-fd_ui").Text));
    }

    [Fact]
    public void ADashboardTheTopologyListsIsCheckedAndTheSiteOfThisPageStillSaysSo()
    {
        // Two homes of the dashboard, both nodes of the topology: one in the cluster, one outside it.
        var manifest = new RuntimeManifest(
            "uat",
            [
                new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
                new RuntimeNode("swa_dashboard", RuntimeNodeKind.StaticSite, "uat/dashboard", new Uri("https://dashboard.uat.example.net"), "dashboard"),
                new RuntimeNode("swa_status", RuntimeNodeKind.StaticSite, "swa-uat-status", new Uri("https://status.example.net"), "status"),
            ],
            [],
            [
                new RuntimeEdge("browser-to-swa_dashboard", "browser", "swa_dashboard", RuntimeEdgeKind.Dashboard),
                new RuntimeEdge("browser-to-swa_status", "browser", "swa_status", RuntimeEdgeKind.Dashboard),
            ]);
        var uat = new EnvironmentStatus(TopologyParser.Parse("""
            { "environments": [ { "name": "uat", "deployables": [
                { "name": "dashboard", "frontDoor": null, "healthPath": "/", "alivePath": "/", "versionPath": "/version.json",
                  "nodes": [ { "name": "uat/dashboard", "role": "primary", "url": "https://dashboard.uat.example.net" } ] },
                { "name": "status", "frontDoor": null, "healthPath": "/", "alivePath": "/", "versionPath": "/version.json",
                  "nodes": [ { "name": "swa-uat-status", "role": "primary", "url": "https://status.example.net" } ] } ] } ] }
            """).Topology!.Environments[0]);
        uat.Deployables[0].Nodes[0].Record(Answer(200, "1.0.7"));
        uat.Deployables[1].Nodes[0].Record(NoAnswer);

        // Seen from the site outside the cluster: the one in the cluster is checked, and answers.
        var outside = RuntimePayloadBuilder.Build(manifest, uat, new Uri("https://status.example.net/"), TimeZoneInfo.Utc);
        Assert.Equal(("healthy", "Healthy"), (Tile(outside, "swa_dashboard").State, Tile(outside, "swa_dashboard").Label));
        Assert.Contains(Tile(outside, "swa_dashboard").Lines, line => line.Text.Contains("1.0.7", StringComparison.Ordinal));
        Assert.Equal("active", Edge(outside, "browser-to-swa_dashboard"));
        Assert.Equal("This page", Tile(outside, "swa_status").Label);

        // Seen from the site in the cluster: it is this page, and the one outside does not answer.
        var inside = RuntimePayloadBuilder.Build(manifest, uat, new Uri("https://dashboard.uat.example.net/"), TimeZoneInfo.Utc);
        Assert.Equal("This page", Tile(inside, "swa_dashboard").Label);
        Assert.Equal("unreachable", Tile(inside, "swa_status").State);
        Assert.Equal("down", Edge(inside, "browser-to-swa_status"));
        Assert.Contains("does not answer", inside.Edges.Single(edge => edge.Id == "browser-to-swa_status").Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cluster's environment: the app is one node at one public address, without a Front Door endpoint, and the
    /// database runs next to it. The diagram has the kinds the page already updates.
    /// </summary>
    private static readonly RuntimeManifest ClusterManifest = new(
        "uat",
        [
            new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
            new RuntimeNode("app_ui_primary", RuntimeNodeKind.WebApp, "uat-ui", new Uri("https://ui.uat.example.net"), "ui", "primary", "westus3", "region_primary"),
            new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "sql-uat", null, null, null, "westus3", "region_primary"),
        ],
        [new RuntimeRegion("region_primary", "westus3", ["primary", "data"])],
        [
            new RuntimeEdge("browser-to-app_ui_primary", "browser", "app_ui_primary", RuntimeEdgeKind.Public),
            new RuntimeEdge("app_ui_primary-to-sqldb", "app_ui_primary", "sqldb", RuntimeEdgeKind.Sql),
        ]);

    private static readonly string[] WordsOfSeveralNodes = ["primary", "standby", "region", "failover", "failed over", "Front Door"];

    private static EnvironmentStatus Cluster(string? frontDoor = null)
    {
        var address = frontDoor is null ? "null" : $"\"{frontDoor}\"";
        return new EnvironmentStatus(TopologyParser.Parse($$"""
            { "environments": [ { "name": "uat", "deployables": [ {
                "name": "ui", "frontDoor": {{address}},
                "nodes": [ { "name": "uat-ui", "role": "primary", "url": "https://ui.uat.example.net" } ] } ] } ] }
            """).Topology!.Environments[0]);
    }

    [Theory]
    [InlineData(null, "checking", "checking", "muted")]
    [InlineData(200, "healthy", "serves traffic", "serving")]
    [InlineData(503, "unhealthy", "not serving", "plain")]
    [InlineData(0, "unreachable", "not serving", "plain")]
    public void TheOnlyNodeOfADeployableWithoutAFrontDoorServesOrDoesNotAndHasNoRole(int? status, string state, string line, string tone)
    {
        var uat = Cluster();
        if (status is { } answered)
        {
            uat.Deployables[0].Nodes[0].Record(answered == 0 ? NoAnswer : Answer(answered));
        }

        var payload = RuntimePayloadBuilder.Build(ClusterManifest, uat, Page, TimeZoneInfo.Utc);

        var tile = Tile(payload, "app_ui_primary");
        Assert.Equal(state, tile.State);
        Assert.Equal(new RuntimeTileLine(line, tone), tile.Lines[^1]);
        Assert.All(WordsOfSeveralNodes, word =>
        {
            Assert.All(tile.Lines, text => Assert.DoesNotContain(word, text.Text, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(word, tile.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, uat.Deployables[0].Assess().Headline, StringComparison.OrdinalIgnoreCase);
        });

        // The states of the diagram are the ones a Front Door system has.
        var expected = status switch { null => "checking", 200 => "active", _ => "down" };
        Assert.Equal(expected, Edge(payload, "browser-to-app_ui_primary"));
        Assert.Equal(status switch { null => "checking", 200 => "serving", _ => "down" }, Region(payload, "region_primary"));
    }

    [Fact]
    public void AnOnlyNodeBehindAFrontDoorKeepsItsRole()
    {
        var uat = Cluster("https://fd-uat.example.net");
        uat.Deployables[0].FrontDoor!.Record(Answer(200));
        uat.Deployables[0].Nodes[0].Record(Answer(200));
        var tdd = Environment("tdd");
        tdd.Deployables[0].Nodes[0].Record(Answer(503));

        Assert.Equal(new RuntimeTileLine("primary: serves traffic", "serving"), Tile(RuntimePayloadBuilder.Build(ClusterManifest, uat, Page, TimeZoneInfo.Utc), "app_ui_primary").Lines[^1]);

        // The sample's tdd: one web app behind its Front Door endpoint.
        Assert.Equal(new RuntimeTileLine("primary: not serving", "plain"), Tile(Build(tdd, "tdd"), "app_ui_primary").Lines[^1]);
        Assert.Equal(new RuntimeTileLine("primary", "muted"), Tile(Build(Environment("tdd"), "tdd"), "app_ui_primary").Lines[^1]);
    }

    [Fact]
    public void TheDatabasesWordsNameNoProduct()
    {
        var uat = Cluster();
        uat.Deployables[0].Nodes[0].Record(Answer(200));
        var unused = ClusterManifest with { Edges = [.. ClusterManifest.Edges.Where(edge => edge.Kind != RuntimeEdgeKind.Sql)] };

        var reachable = Tile(RuntimePayloadBuilder.Build(ClusterManifest, uat, Page, TimeZoneInfo.Utc), "sqldb");
        var notProbed = Tile(RuntimePayloadBuilder.Build(unused, uat, Page, TimeZoneInfo.Utc), "sqldb");

        Assert.Equal(("healthy", "Reachable"), (reachable.State, reachable.Label));
        Assert.Equal("sql-uat: reachable. The database takes no call from a browser; the health check of uat-ui connected to it (last 22:00:00).", reachable.Title);
        Assert.Equal(new RuntimeTileLine("health check of uat-ui passed", "ok"), Assert.Single(reachable.Lines));
        Assert.Equal("sql-uat: the database takes no call from a browser, and this page checks no web app that uses it.", notProbed.Title);

        // The same words in a Front Door system.
        Assert.StartsWith("sqldb-cmdemo2-uat: reachable. The database takes no call from a browser; the health check of ", Tile(Build(Uat()), "sqldb").Title, StringComparison.Ordinal);
    }

    /// <summary>The marks of an environment as the runtime view asks for them: for the deployables its diagram draws.</summary>
    private static IReadOnlyList<DeploymentMark> Deploying(RuntimeManifest manifest, string slug, string deployments) =>
        DeploymentsReport.Parse($$"""{ "deployments": [ {{deployments}} ] }""")!.Marks(manifest.Environment, slug, RuntimePayloadBuilder.Deployables(manifest), Now);

    [Fact]
    public void ANodeOfADeployableThatIsBeingDeployedCarriesTheMarkAndAnotherDoesNot()
    {
        var marks = Deploying(Manifest("uat"), "cmdemo2", """
            { "project": "cmdemo2-ui", "environment": "uat", "release": "2.4.43", "state": "executing", "since": "2026-10-04T21:57:00Z", "url": "https://octopus.example.net/app#/Spaces-1/tasks/ServerTasks-1" },
            { "project": "cmdemo2-ui", "environment": "tdd", "release": "2.4.44", "state": "queued", "since": "2026-10-04T21:59:00Z" }
            """);

        var payload = RuntimePayloadBuilder.Build(Manifest("uat"), Uat(), Page, TimeZoneInfo.Utc, marks);

        // Every node the manifest draws for the deployable: its web apps and its Front Door endpoint.
        const string Sentence = "deploying cmdemo2-ui 2.4.43 to uat (3 min so far)";
        var mark = new RuntimeDeployment(
            "executing",
            Sentence,
            new RuntimeLink("https://octopus.example.net/app#/Spaces-1/tasks/ServerTasks-1", $"{Sentence}\nThe deployment's task in Octopus Deploy (opens in a new tab; Octopus Deploy asks you to sign in)"));
        Assert.All(CheckedAliases, alias =>
        {
            Assert.Equal(mark, Tile(payload, alias).Deployment);
            Assert.EndsWith($"\n{Sentence}", Tile(payload, alias).Title, StringComparison.Ordinal);
        });

        // The database is no deployable's, and nothing deploys the dashboard: neither is marked, and nothing else changed.
        Assert.Null(Tile(payload, "sqldb").Deployment);
        Assert.Null(Tile(payload, "swa_dashboard").Deployment);
        Assert.Equal(Build(Uat()).Regions, payload.Regions);
        Assert.Equal("healthy", Tile(payload, "app_ui_primary").State);

        // Without the file, and for an environment nothing is deployed to, no node carries a mark.
        Assert.All(Build(Uat()).Nodes, tile => Assert.Null(tile.Deployment));
        Assert.All(RuntimePayloadBuilder.Build(Manifest("uat"), Uat(), Page, TimeZoneInfo.Utc, []).Nodes, tile => Assert.Null(tile.Deployment));

        using var json = JsonDocument.Parse(payload.ToJson());
        var drawn = json.RootElement.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("alias").GetString() == "fd_ui").GetProperty("deployment");
        Assert.Equal(["state", "title", "link"], drawn.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["href", "title"], drawn.GetProperty("link").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void TheFirstMarkOfADeployableGivesTheDotItsShapeAndTheTitleHasEveryOne()
    {
        var marks = Deploying(Manifest("uat"), "cmdemo2", """
            { "project": "cmdemo2-ui", "environment": "uat", "release": "2.4.42", "state": "succeeded", "since": "2026-10-04T21:40:00Z", "finished": "2026-10-04T21:58:00Z" },
            { "project": "cmdemo2-ui", "environment": "uat", "release": "2.4.43", "state": "waiting", "since": "2026-10-04T21:59:30Z" }
            """);

        var mark = Tile(RuntimePayloadBuilder.Build(Manifest("uat"), Uat(), Page, TimeZoneInfo.Utc, marks), "app_ui_primary").Deployment;

        // Without an address of the task the dot is no link.
        Assert.Equal(
            new RuntimeDeployment("waiting", "cmdemo2-ui 2.4.43 waits for a sign-off in uat (1 min so far)\ncmdemo2-ui 2.4.42 reached uat 2 min ago"),
            mark);
    }

    [Fact]
    public void ANodeTheTopologyDoesNotHaveCarriesTheMarkOfItsDeployable()
    {
        // The dashboard's static site: the sample topology lists no deployable "dashboard", and the diagram draws its node.
        Assert.Equal(["ui", "dashboard"], RuntimePayloadBuilder.Deployables(Manifest("uat")));
        var dashboard = Deploying(Manifest("uat"), "cmdemo2", """
            { "project": "cmdemo2-dashboard", "environment": "uat", "release": "1.0.8", "state": "succeeded", "since": "2026-10-04T21:50:00Z", "finished": "2026-10-04T21:58:00Z" }
            """);
        var payload = RuntimePayloadBuilder.Build(Manifest("uat"), Uat(), Page, TimeZoneInfo.Utc, dashboard);
        Assert.Equal(new RuntimeDeployment("ended", "cmdemo2-dashboard 1.0.8 reached uat 2 min ago"), Tile(payload, "swa_dashboard").Deployment);
        Assert.All(CheckedAliases, alias => Assert.Null(Tile(payload, alias).Deployment));

        // A node an application recorded for itself (it brings its own runtime): the topology has no environment for
        // it, and the mark is found by the deployable the manifest names. What the deployable depends on is not deployed.
        var own = new RuntimeManifest(
            "prod",
            [
                new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
                new RuntimeNode("site_web", RuntimeNodeKind.Other, "web-prod", null, "Web"),
                new RuntimeNode("dep_web_mail", RuntimeNodeKind.Dependency, "Mail", null, "Web"),
            ],
            [],
            []);
        var recorded = RuntimePayloadBuilder.Build(
            own,
            null,
            Page,
            TimeZoneInfo.Utc,
            Deploying(own, "demo", """{ "project": "demo-web", "environment": "prod", "release": "3.1.0", "state": "queued", "since": "2026-10-04T21:59:30Z" }"""));

        Assert.Equal(new RuntimeDeployment("queued", "demo-web 3.1.0 is queued for prod (1 min so far)"), Tile(recorded, "site_web").Deployment);
        Assert.Equal("web-prod\ndemo-web 3.1.0 is queued for prod (1 min so far)", Tile(recorded, "site_web").Title);
        Assert.Null(Tile(recorded, "dep_web_mail").Deployment);
    }

    [Fact]
    public void TheSystemsOwnReleaseMarksTheEnvironmentAndNoNode()
    {
        var marks = Deploying(Manifest("uat"), "cmdemo2", """
            { "project": "cmdemo2-system", "environment": "uat", "release": "1.0.34", "state": "executing", "since": "2026-10-04T21:57:00Z" },
            { "project": "cmdemo2-reports", "environment": "uat", "release": "0.1.0", "state": "queued", "since": "2026-10-04T21:57:00Z" }
            """);

        // The environment has both marks: the view shows them at the diagram's title and at the environment's button.
        Assert.Equal(
            [("deploying cmdemo2-system 1.0.34 to uat", true, true), ("cmdemo2-reports 0.1.0 is queued for uat", false, true)],
            marks.Select(mark => (mark.Sentence, mark.OfSystem, mark.InFlight)));
        Assert.All(marks, mark => Assert.Null(mark.Deployable));

        // No node is the system's, or a project's the diagram does not draw.
        Assert.All(RuntimePayloadBuilder.Build(Manifest("uat"), Uat(), Page, TimeZoneInfo.Utc, marks).Nodes, tile => Assert.Null(tile.Deployment));
    }

    [Fact]
    public void ThePayloadIsCamelCaseJsonWithoutNulls()
    {
        using var json = JsonDocument.Parse(Build(Environment("uat")).ToJson());

        var root = json.RootElement;
        Assert.Equal(["nodes", "regions", "edges", "names"], root.EnumerateObject().Select(property => property.Name));
        var sql = root.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("alias").GetString() == "sqldb");
        // The sample topology has a link to the database: the name of its node leads there.
        Assert.Equal(["alias", "state", "label", "lines", "title", "nameLink"], sql.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["href", "title"], sql.GetProperty("nameLink").EnumerateObject().Select(property => property.Name));
        Assert.Equal("muted", sql.GetProperty("lines")[0].GetProperty("tone").GetString());
        var edge = root.GetProperty("edges")[0];
        Assert.Equal(["id", "state", "number", "unit", "title"], edge.EnumerateObject().Select(property => property.Name));
    }
}
