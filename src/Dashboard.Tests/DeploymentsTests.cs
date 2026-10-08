using System.Net;

namespace Dashboard.Tests;

public class DeploymentsTests
{
    /// <summary>A minute after the sample was written.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 4, 23, 24, TimeSpan.Zero);
    private static readonly string[] Ui = ["ui"];
    private const string Slug = "demo";
    private const string Tasks = "https://octopus.example.net/app#/Spaces-356/tasks/ServerTasks-";
    private const string DeploymentsPath = "/org/demo-system/deployments/deployments.json";
    private readonly SignallingTimeProvider _time = new();

    /// <summary>The file as the system repository's <c>write-deployments.ps1</c> writes it: in flight first.</summary>
    private const string Sample = $$"""
        { "generated": "2026-10-08T04:22:24Z", "system": "demo", "octopus": "https://octopus.example.net/app#/Spaces-356",
          "deployments": [
            { "project": "demo-ui", "environment": "prod", "release": "2.4.42", "state": "waiting", "since": "2026-10-08T02:00:00Z", "url": "{{Tasks}}3" },
            { "project": "demo-ui", "environment": "uat", "release": "2.4.43", "state": "executing", "since": "2026-10-08T04:20:07Z", "url": "{{Tasks}}1" },
            { "project": "demo-system", "environment": "uat", "release": "1.0.34", "state": "queued", "since": "2026-10-08T04:21:00Z", "url": "{{Tasks}}2" },
            { "project": "demo-ui", "environment": "tdd", "release": "2.4.43", "state": "succeeded", "since": "2026-10-08T04:15:00Z", "finished": "2026-10-08T04:18:00Z", "url": "{{Tasks}}4" } ] }
        """;

    private static readonly DeploymentsReport Report = DeploymentsReport.Parse(Sample)!;

    /// <summary><see cref="Optics.Topology"/> with the address of the deployments file.</summary>
    private static readonly string TopologyWithDeployments = Optics.Topology.Replace(
        "\"deliveryUrl\"",
        $"\"deploymentsUrl\": \"https://raw.example.net{DeploymentsPath}\", \"deliveryUrl\"",
        StringComparison.Ordinal);

    /// <summary>A file with one deployment to uat.</summary>
    private static DeploymentsReport One(string state, string? finished = null, string project = "demo-ui") =>
        DeploymentsReport.Parse($$"""
            { "deployments": [ { "project": "{{project}}", "environment": "uat", "release": "2.4.43", "state": "{{state}}", "since": "2026-10-08T04:20:07Z", "finished": {{(finished is null ? "null" : $"\"{finished}\"")}} } ] }
            """)!;

    private static IReadOnlyList<DeploymentMark> Marks(DeploymentsReport report, DateTimeOffset now, string environment = "uat") =>
        report.Marks(environment, Slug, Ui, now);

    private static HttpResponseMessage Answer(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath == DeploymentsPath ? StubHandler.Answer(HttpStatusCode.OK, Sample) : Optics.Answer(request);

    [Fact]
    public void TheDeploymentsFileIsRead()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 4, 22, 24, TimeSpan.Zero), Report.Generated);
        Assert.Equal(["prod", "uat", "uat", "tdd"], Report.Deployments.Select(deployment => deployment.Environment));
        Assert.Equal(
            new Deployment("demo-ui", "uat", "2.4.43", DeploymentState.Executing, "executing", new DateTimeOffset(2026, 10, 8, 4, 20, 7, TimeSpan.Zero), null, new Uri($"{Tasks}1")),
            Report.Deployments[1]);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 4, 18, 0, TimeSpan.Zero), Report.Deployments[3].Finished);
    }

    [Fact]
    public void TheMarksOfAnEnvironmentAreItsOwnWithWhatAPersonNeedsToKnow()
    {
        var uat = Marks(Report, Now, "UAT");

        Assert.Equal(
            [
                new DeploymentMark("demo-ui", "ui", "uat", "2.4.43", DeploymentState.Executing, "deploying demo-ui 2.4.43 to uat", 3, new Uri($"{Tasks}1"), InFlight: true, OfSystem: false),
                new DeploymentMark("demo-system", null, "uat", "1.0.34", DeploymentState.Queued, "demo-system 1.0.34 is queued for uat", 2, new Uri($"{Tasks}2"), InFlight: true, OfSystem: true),
            ],
            uat);
        Assert.Equal("demo-ui 2.4.42 waits for a sign-off in prod", Assert.Single(Marks(Report, Now, "prod")).Sentence);

        // What ended is counted from its end, not from its start.
        var tdd = Assert.Single(Marks(Report, Now, "tdd"));
        Assert.Equal(("demo-ui 2.4.43 reached tdd 5 min ago", 5, false), (tdd.Sentence, tdd.Minutes, tdd.InFlight));
        Assert.Empty(Marks(Report, Now, "staging"));
    }

    [Theory]
    [InlineData("queued", null, DeploymentState.Queued, "demo-ui 2.4.43 is queued for uat")]
    [InlineData("executing", null, DeploymentState.Executing, "deploying demo-ui 2.4.43 to uat")]
    [InlineData("waiting", null, DeploymentState.Waiting, "demo-ui 2.4.43 waits for a sign-off in uat")]
    [InlineData("succeeded", "2026-10-08T04:18:00Z", DeploymentState.Succeeded, "demo-ui 2.4.43 reached uat 5 min ago")]
    [InlineData("failed", "2026-10-08T04:18:00Z", DeploymentState.Failed, "demo-ui 2.4.43 failed in uat 5 min ago")]
    [InlineData("canceled", "2026-10-08T04:18:00Z", DeploymentState.Canceled, "demo-ui 2.4.43 was canceled in uat 5 min ago")]
    [InlineData("Executing", null, DeploymentState.Executing, "deploying demo-ui 2.4.43 to uat")]
    public void EachStateHasItsSentence(string state, string? finished, DeploymentState expected, string sentence)
    {
        var mark = Assert.Single(Marks(One(state, finished), Now));

        Assert.Equal((expected, sentence), (mark.State, mark.Sentence));
        Assert.Equal(finished is null, mark.InFlight);
    }

    [Fact]
    public void AStateThePageDoesNotKnowIsSaidInTheFilesOwnWord()
    {
        // In flight while the file gives it no end; with an end it is marked as what ended is.
        var paused = Assert.Single(Marks(One("paused"), Now));
        Assert.Equal((DeploymentState.Unknown, "demo-ui 2.4.43 in uat: paused", true), (paused.State, paused.Sentence, paused.InFlight));
        Assert.Equal("demo-ui 2.4.43 in uat: 3", Assert.Single(Marks(One("3"), Now)).Sentence);

        var abandoned = One("abandoned", "2026-10-08T04:18:00Z");
        Assert.False(Assert.Single(Marks(abandoned, Now)).InFlight);
        Assert.Empty(Marks(abandoned, Now.AddMinutes(10)));
    }

    [Theory]
    [InlineData(-90, "1 min")]
    [InlineData(0, "1 min")]
    [InlineData(59, "1 min")]
    [InlineData(5 * 60, "5 min")]
    [InlineData((60 * 60) - 1, "59 min")]
    [InlineData(60 * 60, "1 h")]
    [InlineData(5 * 60 * 60, "5 h")]
    [InlineData((48 * 60 * 60) - 1, "47 h")]
    [InlineData(48 * 60 * 60, "2 d")]
    [InlineData(3 * 24 * 60 * 60, "3 d")]
    public void AnAgeIsMinutesUnderAnHourThenHoursUnderTwoDaysThenDays(int seconds, string expected) =>
        Assert.Equal(expected, DeploymentText.Age(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("404: Not Found")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("""{ "generated": "2026-10-08T04:22:24Z", "system": "demo", "deployments": "none" }""")]
    [InlineData("""{ "deployments": null }""")]
    [InlineData("""{ "deployments": [ { "project": "demo-ui" """)]
    public void AnythingButTheFileIsNoReport(string? body) => Assert.Null(DeploymentsReport.Parse(body));

    [Fact]
    public void AnEmptyListIsAReportThatMarksNothing()
    {
        var report = DeploymentsReport.Parse("""{ "generated": "2026-10-08T04:22:24Z", "deployments": [] }""")!;

        Assert.Empty(report.Deployments);
        Assert.Empty(Marks(report, Now));
    }

    [Fact]
    public void AnEntryWithoutItsNamesIsLeftOutAndATimeThatDoesNotParseIsNoTime()
    {
        var report = DeploymentsReport.Parse("""
            { "generated": "this morning",
              "deployments": [
                7, null, "demo-ui", [ ],
                { "environment": "uat", "release": "1", "state": "executing" },
                { "project": "demo-ui", "release": "2", "state": "executing" },
                { "project": "demo-ui", "environment": "uat", "state": "executing" },
                { "project": "demo-ui", "environment": "uat", "release": "4" },
                { "project": "demo-ui", "environment": "uat", "release": 5, "state": "executing" },
                { "project": "demo-ui", "environment": "uat", "release": "6", "state": "executing", "since": "a while ago", "url": "tasks/ServerTasks-6" },
                { "project": "demo-ui", "environment": "uat", "release": "7", "state": "succeeded", "since": "2026-10-08T04:15:00Z", "finished": "just now" },
                { "project": "demo-ui", "environment": "uat", "release": "8", "state": "queued", "since": 1759897207, "finished": null, "url": "javascript:alert(1)" } ] }
            """)!;

        Assert.Null(report.Generated);
        Assert.Equal(["6", "7", "8"], report.Deployments.Select(deployment => deployment.Release));

        // In flight without a time it can read, a deployment is still marked; one that ended at no time it can read is not.
        var marks = Marks(report, Now);
        Assert.Equal(["6", "8"], marks.Select(mark => mark.Release));
        Assert.All(marks, mark =>
        {
            Assert.Null(mark.Minutes);
            Assert.Null(mark.Url);
            Assert.Equal(mark.Sentence, DeploymentText.Title(mark));
        });
    }

    [Fact]
    public void WhatEndedIsMarkedForTenMinutesAndThenNoMore()
    {
        var ended = new DateTimeOffset(2026, 10, 8, 4, 18, 0, TimeSpan.Zero);
        var report = One("failed", "2026-10-08T04:18:00Z");

        var atOnce = Assert.Single(Marks(report, ended));
        Assert.Equal(("demo-ui 2.4.43 failed in uat 1 min ago", 0), (atOnce.Sentence, atOnce.Minutes));

        var last = Assert.Single(Marks(report, ended + DeploymentsReport.EndedFor - TimeSpan.FromSeconds(1)));
        Assert.Equal(("demo-ui 2.4.43 failed in uat 9 min ago", 9), (last.Sentence, last.Minutes));

        Assert.Empty(Marks(report, ended + DeploymentsReport.EndedFor));
        Assert.Empty(Marks(report, ended.AddDays(1)));

        // A browser whose clock is behind the system's: it ended "ahead" of now, and is marked.
        var ahead = Assert.Single(Marks(report, ended.AddMinutes(-2)));
        Assert.Equal(("demo-ui 2.4.43 failed in uat 1 min ago", 0), (ahead.Sentence, ahead.Minutes));
    }

    [Fact]
    public void WhatIsInFlightIsMarkedHoweverLongItTakes()
    {
        var waiting = Assert.Single(Marks(One("waiting"), Now.AddDays(3)));

        Assert.Equal("demo-ui 2.4.43 waits for a sign-off in uat", waiting.Sentence);
        Assert.Equal((3 * 24 * 60) + 3, waiting.Minutes);
        Assert.Equal("demo-ui 2.4.43 waits for a sign-off in uat (3 d so far)", DeploymentText.Title(waiting));
        Assert.Equal("deploying demo-ui 2.4.43 to uat (3 min so far)", DeploymentText.Title(Assert.Single(Marks(One("executing"), Now))));

        // What ended says its age in the sentence: the title adds nothing.
        var reached = Assert.Single(Marks(One("succeeded", "2026-10-08T04:18:00Z"), Now));
        Assert.Equal(reached.Sentence, DeploymentText.Title(reached));
    }

    [Fact]
    public void WhatAPersonHasToActOnComesFirstThenWhatRunsThenWhatEnded()
    {
        var report = DeploymentsReport.Parse("""
            { "deployments": [
                { "project": "demo-a", "environment": "uat", "release": "1", "state": "succeeded", "finished": "2026-10-08T04:22:00Z" },
                { "project": "demo-b", "environment": "uat", "release": "1", "state": "retired", "finished": "2026-10-08T04:22:00Z" },
                { "project": "demo-c", "environment": "uat", "release": "1", "state": "canceled", "finished": "2026-10-08T04:22:00Z" },
                { "project": "demo-d", "environment": "uat", "release": "1", "state": "failed", "finished": "2026-10-08T04:22:00Z" },
                { "project": "demo-e", "environment": "uat", "release": "1", "state": "paused" },
                { "project": "demo-f", "environment": "uat", "release": "1", "state": "queued" },
                { "project": "demo-g", "environment": "uat", "release": "1", "state": "executing" },
                { "project": "demo-h", "environment": "uat", "release": "1", "state": "waiting" },
                { "project": "demo-i", "environment": "uat", "release": "1", "state": "executing" } ] }
            """)!;

        var marks = Marks(report, Now);

        // Two of one state keep the file's order: the one that started first.
        Assert.Equal(
            ["demo-h", "demo-g", "demo-i", "demo-f", "demo-e", "demo-d", "demo-c", "demo-a", "demo-b"],
            marks.Select(mark => mark.Project));
        Assert.Equal([true, true, true, true, true, false, false, false, false], marks.Select(mark => mark.InFlight));
    }

    [Theory]
    [InlineData("demo-ui", "ui", false)]
    [InlineData("DEMO-UI", "ui", false)]
    [InlineData("demo-api", "Api", false)]
    [InlineData("demo-system", null, true)]
    [InlineData("Demo-System", null, true)]
    [InlineData("demo-reports", null, false)]
    [InlineData("other-ui", null, false)]
    [InlineData("demo", null, false)]
    [InlineData("demo-", null, false)]
    [InlineData("ui", null, false)]
    public void AProjectIsItsDeployableAfterTheSlugAndTheSystemsOwnIsNone(string project, string? deployable, bool ofSystem)
    {
        var mark = Assert.Single(One("executing", project: project).Marks("uat", Slug, ["ui", "Api"], Now));

        Assert.Equal((project, deployable, ofSystem), (mark.Project, mark.Deployable, mark.OfSystem));
    }

    [Fact]
    public void WithoutASlugOrADeployableNoProjectIsADeployable()
    {
        Assert.Null(Assert.Single(One("executing").Marks("uat", string.Empty, Ui, Now)).Deployable);
        Assert.Null(Assert.Single(One("executing").Marks("uat", Slug, [], Now)).Deployable);
        Assert.False(Assert.Single(One("executing", project: "-system").Marks("uat", string.Empty, Ui, Now)).OfSystem);
        Assert.Equal("ui", DeploymentMark.NameOf("demo-ui", "demo"));
        Assert.Equal("my-app", DeploymentMark.NameOf("demo-my-app", "demo"));
        Assert.Null(DeploymentMark.NameOf("demonstration", "demo"));
    }

    [Theory]
    [InlineData(DeploymentState.Executing, "executing")]
    [InlineData(DeploymentState.Queued, "queued")]
    [InlineData(DeploymentState.Waiting, "waiting")]
    [InlineData(DeploymentState.Succeeded, "ended")]
    [InlineData(DeploymentState.Failed, "ended")]
    [InlineData(DeploymentState.Canceled, "ended")]
    [InlineData(DeploymentState.Unknown, "ended")]
    public void TheDotHasAShapePerStateInFlightAndOneForWhatEnded(DeploymentState state, string shape) =>
        Assert.Equal(shape, DeploymentText.Shape(state));

    [Fact]
    public void TheHelpSaysTheMarksAreNotLiveAndHowOld()
    {
        Assert.StartsWith("Not live: what the system itself read from Octopus Deploy, as of 2026-10-08 04:22 +00:00. A deployment that started shows within", DeploymentText.Help(Report.Generated, TimeZoneInfo.Utc), StringComparison.Ordinal);
        Assert.StartsWith("Not live: what the system itself read from Octopus Deploy. A deployment", DeploymentText.Help(null, TimeZoneInfo.Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTopologyCarriesTheDeploymentsAddressAndRefusesOneThatIsNoAddress()
    {
        Assert.Equal($"https://raw.example.net{DeploymentsPath}", TopologyParser.Parse(TopologyWithDeployments).Topology!.System.DeploymentsUrl?.AbsoluteUri);
        Assert.Null(TopologyParser.Parse(Optics.Topology).Topology!.System.DeploymentsUrl);
        Assert.Null(TopologyParser.Parse("""{ "system": { "deploymentsUrl": null }, "environments": [] }""").Topology!.System.DeploymentsUrl);
        Assert.Equal(
            ["system.deploymentsUrl: not an absolute http or https address."],
            TopologyParser.Parse("""{ "system": { "deploymentsUrl": "deployments.json" }, "environments": [] }""").Errors);
        Assert.Equal(
            ["system.deploymentsUrl: not an absolute http or https address."],
            TopologyParser.Parse("""{ "system": { "deploymentsUrl": "ftp://raw.example.net/deployments.json" }, "environments": [] }""").Errors);
    }

    [Fact]
    public async Task TheFileIsReadWithTheFirstRoundAndThenEveryMinute()
    {
        var handler = new StubHandler(Answer);
        var monitor = Optics.Monitor(handler, _time, topology: TopologyWithDeployments);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(4, monitor.Deployments!.Deployments.Count);

        _time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(29));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(1, Optics.Count(handler, DeploymentsPath));

        _time.Advance(TimeSpan.FromSeconds(1));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, Optics.Count(handler, DeploymentsPath));
        Assert.Equal(TimeSpan.FromSeconds(60), DashboardMonitor.DeploymentsInterval);
    }

    [Fact]
    public async Task TheMonitorMarksTheDeployablesOfItsTopologyAndRaisesNoEvent()
    {
        var events = new EventLog();
        var monitor = Optics.Monitor(new StubHandler(Answer), _time, events, TopologyWithDeployments);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        var uat = monitor.DeploymentMarks("uat", monitor.Environments[1].Deployables.Select(deployable => deployable.Info.Name), _time.GetUtcNow());
        Assert.Equal(["demo-ui", "demo-system"], uat.Select(mark => mark.Project));
        Assert.Equal(["ui", null], uat.Select(mark => mark.Deployable));
        Assert.Empty(events.Newest);
        Assert.Equal("All 4 nodes healthy", monitor.Summary.Text);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("not found")]
    [InlineData("not the file")]
    public async Task AReadingThatFailsMarksNothingAndChangesNothingElse(string failure)
    {
        var failing = false;
        var handler = new StubHandler(request => !failing || request.RequestUri!.AbsolutePath != DeploymentsPath
            ? Answer(request)
            : failure switch
            {
                "offline" => throw new HttpRequestException("offline"),
                "not found" => StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found"),
                _ => StubHandler.Answer(HttpStatusCode.OK, "<html>Sign in</html>"),
            });
        var events = new EventLog();
        var monitor = Optics.Monitor(handler, _time, events, TopologyWithDeployments);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.NotEmpty(monitor.DeploymentMarks("uat", Ui, _time.GetUtcNow()));

        failing = true;
        _time.Advance(DashboardMonitor.DeploymentsInterval);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        // Not the marks of the reading before: a deployment the page can no longer read about would stay marked.
        Assert.Equal(2, Optics.Count(handler, DeploymentsPath));
        Assert.Null(monitor.Deployments);
        Assert.Empty(monitor.DeploymentMarks("uat", Ui, _time.GetUtcNow()));
        Assert.Empty(events.Newest);
        Assert.Equal("All 4 nodes healthy", monitor.Summary.Text);
        Assert.All(monitor.Targets, target => Assert.Equal(HealthState.Healthy, target.State));
        Assert.NotNull(monitor.Delivery);

        // The next reading that succeeds marks again.
        failing = false;
        _time.Advance(DashboardMonitor.DeploymentsInterval);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.NotEmpty(monitor.DeploymentMarks("uat", Ui, _time.GetUtcNow()));
    }

    [Fact]
    public async Task WithoutADeploymentsAddressNothingIsReadAndNothingIsMarked()
    {
        var handler = new StubHandler(Answer);
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Deployments);
        Assert.Empty(monitor.DeploymentMarks("uat", Ui, _time.GetUtcNow()));
        Assert.Equal(0, Optics.Count(handler, DeploymentsPath));
    }
}
