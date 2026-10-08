using System.Net;

namespace Dashboard.Tests;

public class HealthDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private readonly SignallingTimeProvider _time = new();

    /// <summary>The answer of the work-order app's <c>/_healthcheck/detailed</c>, with one entry of each state.</summary>
    internal const string Answer = """
        {
          "serverUtc": "2026-10-04T21:59:59.2199852+00:00", "overallStatus": "Unhealthy", "totalDurationMs": 15046.4,
          "entries": [
            { "name": "API", "status": "Healthy", "description": "API layer is healthy", "durationMs": 0.0009 },
            { "name": "DataAccess", "status": "Unhealthy", "description": "Database connection failed: login timeout expired", "durationMs": 15002.3 },
            { "name": "Jeffrey", "status": "Healthy", "durationMs": 0.0258 },
            { "name": "LlmGateway", "status": "Degraded", "description": "Chat client answered slowly: 4.2 s (threshold 2 s).", "durationMs": 44.07 },
            { "name": "self", "status": "Healthy", "durationMs": 0.0006, "tags": [ "live" ] }
          ]
        }
        """;

    /// <summary>An answer with the given status for every entry named, in that order.</summary>
    internal static string Entries(params (string Name, string Status)[] entries) =>
        $$"""{ "entries": [ {{string.Join(", ", entries.Select(entry => $$"""{ "name": "{{entry.Name}}", "status": "{{entry.Status}}", "description": "{{entry.Name}} says {{entry.Status.ToLowerInvariant()}}", "durationMs": 1.5 }"""))}} ] }""";

    internal static HealthDetail Detail(params (string Name, string Status)[] entries) => HealthDetail.Parse(Entries(entries), Now)!;

    private const string Topology = """
        {
          "environments": [
            { "name": "uat", "deployables": [ {
                "name": "ui", "frontDoor": "https://fd-uat.example.net", "healthDetailPath": "_healthcheck/detailed",
                "nodes": [
                  { "name": "uat-west", "region": "westus3", "role": "primary", "url": "https://uat-west.example.net" },
                  { "name": "uat-east", "region": "eastus2", "role": "standby", "url": "https://uat-east.example.net" } ] } ] },
            { "name": "dev", "deployables": [ {
                "name": "ui", "nodes": [ { "name": "dev-west", "url": "https://dev-west.example.net" } ] } ] }
          ]
        }
        """;

    private const string DetailPath = "/_healthcheck/detailed";

    private static HttpResponseMessage Respond(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        DetailPath when request.RequestUri.Host == "uat-west.example.net" => StubHandler.Answer(HttpStatusCode.ServiceUnavailable, Answer),
        DetailPath => StubHandler.Answer(HttpStatusCode.OK, Entries(("API", "Healthy"), ("DataAccess", "Healthy"))),
        "/_healthcheck" when request.RequestUri.Host == "uat-west.example.net" => StubHandler.Answer(HttpStatusCode.ServiceUnavailable, "Unhealthy"),
        _ => StubHandler.Answer(HttpStatusCode.OK, "Healthy"),
    };

    [Fact]
    public void TheEndpointsAnswerIsReadEntryByEntry()
    {
        var detail = HealthDetail.Parse(Answer, Now)!;

        Assert.Equal(Now, detail.ReadAt);
        Assert.Equal(
            [
                new HealthCheckEntry("API", CheckState.Healthy, "API layer is healthy", 0.0009),
                new HealthCheckEntry("DataAccess", CheckState.Unhealthy, "Database connection failed: login timeout expired", 15002.3),
                new HealthCheckEntry("Jeffrey", CheckState.Healthy, null, 0.0258),
                new HealthCheckEntry("LlmGateway", CheckState.Degraded, "Chat client answered slowly: 4.2 s (threshold 2 s).", 44.07),
                new HealthCheckEntry("self", CheckState.Healthy, null, 0.0006),
            ],
            detail.Entries);
        Assert.Equal(CheckState.Degraded, detail.Find("llmgateway")!.State);
        Assert.Null(detail.Find("Payments"));
        Assert.Null(detail.Find(null));
    }

    [Fact]
    public void TheEntriesThatAreNotHealthyComeWorstFirst()
    {
        var detail = Detail(("A", "Degraded"), ("B", "Healthy"), ("C", "Paused"), ("D", "Unhealthy"), ("E", "degraded"));

        Assert.Equal(["D", "A", "E", "C"], detail.NotHealthy.Select(entry => entry.Name));
        Assert.Equal(CheckState.Unknown, detail.Find("C")!.State);
        Assert.Empty(Detail(("A", "Healthy")).NotHealthy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Healthy")]
    [InlineData("<html>503</html>")]
    [InlineData("[]")]
    [InlineData("""{ "overallStatus": "Healthy" }""")]
    [InlineData("""{ "entries": [] }""")]
    [InlineData("""{ "entries": "none" }""")]
    [InlineData("""{ "entries": [ { "status": "Healthy" }, "API", 3 ] }""")]
    public void AnAnswerThatIsNotTheExpectedJsonIsNoEntries(string answer)
    {
        Assert.Null(HealthDetail.Parse(answer, Now));
    }

    [Fact]
    public void AnEntryWithoutANameIsLeftOutAndOneWithoutAStatusIsNotKnown()
    {
        var detail = HealthDetail.Parse("""{ "entries": [ { "status": "Healthy" }, { "name": "API" }, { "name": "Queue", "status": 3, "durationMs": -1 } ] }""", Now)!;

        Assert.Equal([new HealthCheckEntry("API", CheckState.Unknown), new HealthCheckEntry("Queue", CheckState.Unknown)], detail.Entries);
    }

    [Fact]
    public void AMarkHasItsStateInAWordAndItsOwnWordsInTheTooltip()
    {
        var detail = HealthDetail.Parse(Answer, Now)!;

        Assert.Equal(["Healthy", "Degraded", "Unhealthy", "Not known"], new[] { CheckState.Healthy, CheckState.Degraded, CheckState.Unhealthy, CheckState.Unknown }.Select(HealthDetailText.Label));
        Assert.Equal("API: Healthy. API layer is healthy. Took under 0.1 ms.", HealthDetailText.Title(detail.Entries[0]));
        Assert.Equal("DataAccess: Unhealthy. Database connection failed: login timeout expired. Took 15 s.", HealthDetailText.Title(detail.Entries[1]));
        Assert.Equal("LlmGateway: Degraded. Chat client answered slowly: 4.2 s (threshold 2 s). Took 44.1 ms.", HealthDetailText.Title(detail.Entries[3]));
        Assert.Equal("Queue: Not known.", HealthDetailText.Title(new HealthCheckEntry("Queue", CheckState.Unknown)));
    }

    [Fact]
    public void EveryStateHasAShapeOfItsOwn()
    {
        Assert.Equal(
            [HealthState.Healthy, HealthState.Unhealthy, HealthState.Unreachable, HealthState.Pending],
            new[] { CheckState.Healthy, CheckState.Degraded, CheckState.Unhealthy, CheckState.Unknown }.Select(HealthDetailText.Icon));
    }

    [Theory]
    [InlineData(0.0006, "under 0.1 ms")]
    [InlineData(0.41, "0.4 ms")]
    [InlineData(42.07, "42.1 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(4211.9, "4.2 s")]
    public void ADurationIsShownInMillisecondsAndFromASecondOnInSeconds(double milliseconds, string expected)
    {
        Assert.Equal(expected, HealthDetailText.Duration(milliseconds));
    }

    [Fact]
    public void TheSummaryNamesTheOneEntryThatIsNotHealthyAndCountsMore()
    {
        Assert.Equal("1 check healthy", HealthDetailText.Summary(Detail(("API", "Healthy"))));
        Assert.Equal("3 checks healthy", HealthDetailText.Summary(Detail(("API", "Healthy"), ("DataAccess", "Healthy"), ("self", "Healthy"))));
        Assert.Equal("LlmGateway degraded", HealthDetailText.Summary(Detail(("API", "Healthy"), ("LlmGateway", "Degraded"))));
        Assert.Equal("DataAccess unhealthy", HealthDetailText.Summary(Detail(("DataAccess", "Unhealthy"))));
        Assert.Equal("2 of 5 checks not healthy", HealthDetailText.Summary(HealthDetail.Parse(Answer, Now)!));
        Assert.Equal("AVeryLongNameOfAChe… not known", HealthDetailText.Summary(Detail(("AVeryLongNameOfACheckThatGoesOn", "Paused"))));
    }

    [Theory]
    [InlineData("Chat client is not configured. Set the following environment variables: AI_OpenAI_ApiKey", 36, "Chat client is not configured")]
    [InlineData("Process has 24 threads", 36, "Process has 24 threads")]
    [InlineData("Process memory is healthy.", 36, "Process memory is healthy")]
    [InlineData("Database connection successful (Provider: Microsoft.EntityFrameworkCore.SqlServer)", 36, "Database connection successful (Pro…")]
    public void WordsAreCutToTheirFirstSentenceOrWithAnEllipsis(string text, int length, string expected)
    {
        var brief = HealthDetailText.Brief(text, length);

        Assert.Equal(expected, brief);
        Assert.True(brief.Length <= length);
    }

    [Fact]
    public void WhatFailedIsNamedWithItsStateAndItsOwnWords()
    {
        Assert.Equal(
            "Not healthy: DataAccess (Unhealthy: Database connection failed: login timeout expired), LlmGateway (Degraded: Chat client answered slowly: 4.2 s (threshold 2 s))",
            HealthDetailText.Failed(HealthDetail.Parse(Answer, Now)));
        Assert.Equal("Not healthy: Queue (Not known)", HealthDetailText.Failed(HealthDetail.Parse("""{ "entries": [ { "name": "Queue" } ] }""", Now)));
        Assert.Null(HealthDetailText.Failed(Detail(("API", "Healthy"))));
        Assert.Null(HealthDetailText.Failed(null));
    }

    [Fact]
    public void TheTopologyCarriesThePathAndATopologyWithoutItHasNone()
    {
        var topology = TopologyParser.Parse(Topology).Topology!;

        Assert.Equal(DetailPath, topology.Environments[0].Deployables[0].HealthDetailPath);
        Assert.Null(topology.Environments[1].Deployables[0].HealthDetailPath);
        Assert.Null(TopologyParser.Parse("""{ "environments": [ { "name": "uat", "deployables": [ { "healthDetailPath": null, "nodes": [] } ] } ] }""").Topology!.Environments[0].Deployables[0].HealthDetailPath);
    }

    [Fact]
    public async Task TheProberReadsTheAnswerOfAFailingHealthCheckToo()
    {
        var prober = new NodeProber(new HttpClient(new StubHandler(Respond)), _time);

        var failing = await prober.ReadHealthDetailAsync(new Uri("https://uat-west.example.net"), DetailPath, CancellationToken.None);
        var passing = await prober.ReadHealthDetailAsync(new Uri("https://uat-east.example.net"), DetailPath, CancellationToken.None);

        Assert.Equal(["DataAccess", "LlmGateway"], failing!.NotHealthy.Select(entry => entry.Name));
        Assert.Equal(_time.GetUtcNow(), failing.ReadAt);
        Assert.Empty(passing!.NotHealthy);
    }

    [Fact]
    public async Task ANodeThatDoesNotAnswerOrAnswersSomethingElseHasNoEntries()
    {
        var offline = new NodeProber(new HttpClient(new StubHandler(_ => throw new HttpRequestException("TypeError: Failed to fetch"))), _time);
        var other = new NodeProber(new HttpClient(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.NotFound, "<html>404</html>"))), _time);

        Assert.Null(await offline.ReadHealthDetailAsync(new Uri("https://uat-west.example.net"), DetailPath, CancellationToken.None));
        Assert.Null(await other.ReadHealthDetailAsync(new Uri("https://uat-west.example.net"), DetailPath, CancellationToken.None));
    }

    [Fact]
    public async Task ANodeThatTakesLongerThanTheTimeoutHasNoEntries()
    {
        var silent = new NodeProber(new HttpClient(new StubHandler((_, cancellation) => StubHandler.NeverAsync(cancellation))), _time);

        var reading = silent.ReadHealthDetailAsync(new Uri("https://uat-west.example.net"), DetailPath, CancellationToken.None);
        await _time.TimerCreatedAsync();
        _time.Advance(NodeProber.DefaultTimeout);

        Assert.Null(await reading);
    }

    [Fact]
    public async Task EveryRegionalNodeIsAskedWithTheHealthCheckAndTheFrontDoorEndpointIsNot()
    {
        var handler = new StubHandler(Respond);
        var monitor = Optics.Monitor(handler, _time, topology: Topology);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(
            ["https://uat-east.example.net/_healthcheck/detailed", "https://uat-west.example.net/_healthcheck/detailed"],
            handler.RequestedUrls.Where(url => url.EndsWith(DetailPath, StringComparison.Ordinal)).Order());
        var ui = monitor.Environments[0].Deployables[0];
        Assert.Null(ui.FrontDoor!.HealthDetail);
        Assert.Equal(HealthState.Unhealthy, ui.Nodes[0].State);
        Assert.Equal("2 of 5 checks not healthy", HealthDetailText.Summary(ui.Nodes[0].HealthDetail!));
        Assert.Equal("2 checks healthy", HealthDetailText.Summary(ui.Nodes[1].HealthDetail!));

        // A deployable without the path is asked nothing, and its node has no entries.
        Assert.Null(monitor.Environments[1].Deployables[0].Nodes[0].HealthDetail);
        Assert.DoesNotContain(handler.RequestedUrls, url => url.StartsWith("https://dev-west.example.net/_healthcheck/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTheLivenessProbeNothingIsAskedAndTheEntriesOfTheLastHealthCheckAreGone()
    {
        var handler = new StubHandler(Respond);
        var monitor = Optics.Monitor(handler, _time, topology: Topology);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var asked = Optics.Count(handler, DetailPath);

        await monitor.CheckAllAsync(ProbeKind.Liveness, CancellationToken.None);

        Assert.Equal(2, asked);
        Assert.Equal(asked, Optics.Count(handler, DetailPath));
        Assert.All(monitor.Environments[0].Deployables[0].Nodes, node => Assert.Null(node.HealthDetail));
    }

    [Fact]
    public async Task AnAnswerThatCannotBeReadReplacesAGoodOneAndFailsNoCheck()
    {
        var broken = false;
        var handler = new StubHandler(request => broken && request.RequestUri!.AbsolutePath == DetailPath ? StubHandler.Answer(HttpStatusCode.OK, "<html></html>") : Respond(request));
        var monitor = Optics.Monitor(handler, _time, topology: Topology);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        var standby = monitor.Environments[0].Deployables[0].Nodes[1];
        Assert.NotNull(standby.HealthDetail);

        broken = true;
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(standby.HealthDetail);
        Assert.Equal(HealthState.Healthy, standby.State);
    }
}
