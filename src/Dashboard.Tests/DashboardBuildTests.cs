using System.Net;

namespace Dashboard.Tests;

/// <summary>The dashboard's own build facts: <c>system.dashboard</c> of the topology, read from the page's own address.</summary>
public class DashboardBuildTests
{
    private const string Page = "https://dashboard.example.net/";
    private const string FactsPath = "/build-facts.json";

    /// <summary>What the dashboard's Build writes next to <c>index.html</c>: the sections it does not measure are null.</summary>
    private const string Facts = """
        { "version": "1.0.57", "commit": "5c0ffee5c0ffee5c0ffee5c0ffee5c0ffee5c0ff", "commitUrl": "https://github.example.net/o/demo-dashboard/commit/5c0ffee5c0ffee5c0ffee5c0ffee5c0ffee5c0ff",
          "builtAt": "2026-10-04T20:00:00Z", "buildUrl": "https://github.example.net/o/demo-dashboard/actions/runs/57",
          "code": { "linesOfCode": 20100, "files": 160, "languages": [ { "name": "C#", "lines": 14000, "files": 100 }, { "name": "Razor", "lines": 2600, "files": 33 }, { "name": "CSS", "lines": 1900, "files": 1 } ] },
          "tests": { "unit": 760, "integration": null, "acceptance": null },
          "coverage": null, "complexity": null, "crap": null, "analysis": null }
        """;

    private static readonly string TopologyWithDashboard = Optics.Topology.Replace(
        "\"system\": { ",
        "\"system\": { \"dashboard\": { \"name\": \"dashboard\", \"buildPath\": \"/build-facts.json\" }, ",
        StringComparison.Ordinal);

    private readonly SignallingTimeProvider _time = new();

    /// <summary>A monitor whose page is served from <see cref="Page"/>, as the browser's HttpClient is set up.</summary>
    private DashboardMonitor Monitor(StubHandler handler, string? topology = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(Page) };
        return new DashboardMonitor(TopologyParser.Parse(topology ?? TopologyWithDashboard).Topology!, new NodeProber(http, _time), new PinnedVersionsReader(http, _time), _time);
    }

    private static bool IsFacts(HttpRequestMessage request) => request.RequestUri!.Host == "dashboard.example.net";

    [Fact]
    public void TheTopologyNamesTheDashboardAndWhereItServesItsBuildFacts()
    {
        var named = TopologyParser.Parse(TopologyWithDashboard).Topology!.System.Dashboard;
        var plain = TopologyParser.Parse("""{ "system": { "dashboard": { "buildPath": "build-facts.json" } }, "environments": [] }""").Topology!.System.Dashboard;

        Assert.Equal(new DashboardInfo("dashboard", FactsPath), named);
        Assert.Equal(new DashboardInfo(DashboardInfo.DefaultName, FactsPath), plain);
    }

    [Theory]
    [InlineData("""{ "environments": [] }""")]
    [InlineData("""{ "system": { "slug": "demo" }, "environments": [] }""")]
    [InlineData("""{ "system": { "dashboard": null }, "environments": [] }""")]
    [InlineData("""{ "system": { "dashboard": "dashboard" }, "environments": [] }""")]
    [InlineData("""{ "system": { "dashboard": { "name": "dashboard" } }, "environments": [] }""")]
    [InlineData("""{ "system": { "dashboard": { "name": "dashboard", "buildPath": null } }, "environments": [] }""")]
    [InlineData("""{ "system": { "dashboard": { "name": "dashboard", "buildPath": 7 } }, "environments": [] }""")]
    public void ATopologyThatDoesNotNameTheBuildFactsHasNoDashboardAndIsNoError(string json)
    {
        var result = TopologyParser.Parse(json);

        Assert.True(result.IsValid);
        Assert.Null(result.Topology!.System.Dashboard);
    }

    [Fact]
    public void TheSampleShippedWithTheAppNamesNone()
    {
        // A local run has no build-facts.json (the Build writes it into the published site): the sample asks for none.
        var sample = TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json")));

        Assert.Null(sample.Topology!.System.Dashboard);
    }

    [Fact]
    public async Task ThePageReadsItsOwnBuildFactsFromItsOwnAddressOnce()
    {
        var handler = new StubHandler(request => IsFacts(request) ? StubHandler.Answer(HttpStatusCode.OK, Facts) : Optics.Answer(request));
        var monitor = Monitor(handler);
        Assert.Null(monitor.DashboardBuild);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await monitor.CheckAllAsync(ProbeKind.Liveness, CancellationToken.None);

        Assert.Equal([$"{Page}build-facts.json"], handler.RequestedUrls.Where(url => url.StartsWith(Page, StringComparison.Ordinal)));
        var build = monitor.DashboardBuild!;
        Assert.Equal(("1.0.57", "5c0ffee"), (build.Version, BuildText.ShortCommit(build.Commit!)));
        Assert.Equal("20,100 lines in 160 files", BuildText.Size(build.Code!));
        Assert.Equal("760 unit", BuildText.Tests(build.Tests!));
        Assert.Null(build.Coverage);
        Assert.Null(build.QodanaProblems);

        // The apps' cards are their own nodes' answers, as before.
        Assert.All(monitor.Environments, environment => Assert.Equal("2.4.15", environment.Deployables[0].Build!.Version));
    }

    [Fact]
    public async Task AFileThatDoesNotAnswerIsAskedAgainEveryFiveMinutesUntilItDoes()
    {
        var published = false;
        var handler = new StubHandler(request => !IsFacts(request) ? Optics.Answer(request)
            : published ? StubHandler.Answer(HttpStatusCode.OK, Facts) : StubHandler.Answer(HttpStatusCode.NotFound, "<html>404</html>"));
        var monitor = Monitor(handler);
        int Asked() => handler.Requests.Count(IsFacts);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(1, Asked());
        Assert.Null(monitor.DashboardBuild);
        Assert.All(monitor.Targets, target => Assert.Equal(HealthState.Healthy, target.State));

        published = true;
        _time.Advance(DashboardMonitor.DashboardBuildRetry);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        _time.Advance(DashboardMonitor.DashboardBuildRetry);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, Asked());
        Assert.Equal("1.0.57", monitor.DashboardBuild!.Version);
    }

    [Fact]
    public async Task AnAnswerThatIsNotTheFileIsNoBuild()
    {
        // A static-site host may answer a missing file with index.html and HTTP 200.
        var handler = new StubHandler(request => IsFacts(request) ? StubHandler.Answer(HttpStatusCode.OK, "<!DOCTYPE html><html></html>") : Optics.Answer(request));
        var monitor = Monitor(handler);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.DashboardBuild);
    }

    [Fact]
    public async Task WithoutTheKeyNothingIsReadFromThePagesOwnAddress()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Monitor(handler, Optics.Topology);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.DashboardBuild);
        Assert.DoesNotContain(handler.Requests, IsFacts);
    }

    [Theory]
    [InlineData("//other.example.net/facts.json", "https://dashboard.example.net/other.example.net/facts.json")]
    [InlineData("facts/build.json", "https://dashboard.example.net/facts/build.json")]
    [InlineData("https://other.example.net/facts.json", null)]
    public async Task TheFileIsReadFromThePagesOwnSiteWhateverThePathSays(string path, string? asked)
    {
        var handler = new StubHandler(request => request.RequestUri!.Host.EndsWith("example.net", StringComparison.Ordinal) && request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
            ? StubHandler.Answer(HttpStatusCode.OK, Facts)
            : Optics.Answer(request));
        var monitor = Monitor(handler, TopologyWithDashboard.Replace(FactsPath, path, StringComparison.Ordinal));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.DoesNotContain(handler.RequestedUrls, url => url.StartsWith("https://other.example.net", StringComparison.Ordinal));
        Assert.Equal(asked is null ? [] : [asked], handler.RequestedUrls.Where(url => url.StartsWith(Page, StringComparison.Ordinal)));
        Assert.Equal(asked is not null, monitor.DashboardBuild is not null);
    }

    [Fact]
    public void TheCardIsLabelledAsTheDashboardsOwn() =>
        Assert.Equal("dashboard (this page)", BuildText.DashboardContext("dashboard"));
}
