using System.Net;
using Dashboard.Runtime;

namespace Dashboard.Tests;

/// <summary>
/// What the system's deployments file says beyond the state of a deployment, and how the page says it in words: on a
/// node of the runtime view (<see cref="DeploymentActivity.OfNode"/>) and under an environment's name
/// (<see cref="DeploymentActivity.OfEnvironment"/>, <see cref="DeploymentMark.Detail"/>).
/// </summary>
public class DeploymentActivityTests
{
    /// <summary>Friday noon, UTC.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly string[] Ui = ["ui"];
    private static readonly string[] Environments = ["uat", "prod", "tdd"];
    private static readonly string[] Untouched = ["site_api", "dep_ui_mail"];
    private const string Slug = "demo";
    private const string Tasks = "https://octopus.example.net/app#/Spaces-356/tasks/ServerTasks-";
    private const string TaskTitle = "The deployment's task in Octopus Deploy (opens in a new tab; Octopus Deploy asks you to sign in)";
    private const string DeploymentsPath = "/org/demo-system/deployments/deployments.json";

    /// <summary>The file as <c>write-deployments.ps1</c> writes it now: something of everything.</summary>
    private const string Sample = $$"""
        { "generated": "2026-10-09T11:58:00Z", "system": "demo", "octopus": "https://octopus.example.net/app#/Spaces-356",
          "deployments": [
            { "project": "demo-ui", "environment": "prod", "release": "2.4.43", "state": "waiting", "since": "2026-10-09T09:58:00Z", "startedBy": "jeffrey", "url": "{{Tasks}}5",
              "waitsFor": { "kind": "sign-off", "title": "Sign-off", "since": "2026-10-09T10:00:00Z", "responsible": "demo approvers" } },
            { "project": "demo-ui", "environment": "uat", "release": "2.4.44", "state": "executing", "since": "2026-10-09T11:57:00Z", "startedBy": "ai-ops", "url": "{{Tasks}}4" },
            { "project": "demo-system", "environment": "uat", "release": "1.0.7", "state": "failed", "since": "2026-10-09T11:40:00Z", "finished": "2026-10-09T11:55:00Z", "startedBy": "ai-ops", "url": "{{Tasks}}3" } ],
          "recent": [
            { "project": "demo-system", "release": "1.0.7", "environment": "uat", "result": "failed", "finished": "2026-10-09T11:55:00Z", "startedBy": "ai-ops", "url": "{{Tasks}}3" },
            { "project": "demo-ui", "release": "2.4.43", "environment": "uat", "result": "succeeded", "finished": "2026-10-09T07:00:00Z", "startedBy": "ai-ops", "url": "{{Tasks}}2" },
            { "project": "demo-ui", "release": "2.4.40", "environment": "prod", "result": "succeeded", "finished": "2026-10-01T15:00:00Z", "url": "{{Tasks}}1" } ],
          "freezes": [
            { "name": "prod weekend", "from": "2026-10-09T00:00:00Z", "to": "2026-10-12T00:00:00Z", "active": true, "environments": [ "prod" ], "projects": [ "demo-ui" ] },
            { "name": "release week", "from": "2026-10-10T19:00:00Z", "to": "2026-10-11T19:00:00Z", "active": false, "environments": [ "uat", "prod" ], "projects": [ "demo-ui", "demo-system" ] } ],
          "missing": [] }
        """;

    /// <summary>The same deployments as a system wrote them before the new fields.</summary>
    private const string Older = $$"""
        { "generated": "2026-10-09T11:58:00Z", "system": "demo", "octopus": "https://octopus.example.net/app#/Spaces-356",
          "deployments": [
            { "project": "demo-ui", "environment": "prod", "release": "2.4.43", "state": "waiting", "since": "2026-10-09T09:58:00Z", "url": "{{Tasks}}5" },
            { "project": "demo-ui", "environment": "uat", "release": "2.4.44", "state": "executing", "since": "2026-10-09T11:57:00Z", "url": "{{Tasks}}4" },
            { "project": "demo-system", "environment": "uat", "release": "1.0.7", "state": "failed", "since": "2026-10-09T11:40:00Z", "finished": "2026-10-09T11:55:00Z", "url": "{{Tasks}}3" } ] }
        """;

    private static readonly DeploymentsReport Report = DeploymentsReport.Parse(Sample)!;

    private static DeploymentsReport Parse(string deployments = "", string recent = "", string freezes = "") =>
        DeploymentsReport.Parse($$"""{ "deployments": [ {{deployments}} ], "recent": [ {{recent}} ], "freezes": [ {{freezes}} ], "missing": [] }""")!;

    private static IReadOnlyList<ActivityLine> Node(DeploymentsReport report, string environment, string deployable = "ui", DateTimeOffset? now = null) =>
        DeploymentActivity.OfNode(report, environment, Slug, deployable, now ?? Now, Utc);

    private static IReadOnlyList<ActivityLine> Environment(DeploymentsReport report, string environment, DateTimeOffset? now = null) =>
        DeploymentActivity.OfEnvironment(report, environment, report.Marks(environment, Slug, Ui, now ?? Now), now ?? Now, Utc);

    private static string Waiting(string waitsFor, string more = "") =>
        $$"""{ "project": "demo-ui", "environment": "prod", "release": "2.4.43", "state": "waiting", "since": "2026-10-09T09:58:00Z"{{more}}, "waitsFor": {{waitsFor}} }""";

    [Fact]
    public void TheNewPartsOfTheFileAreRead()
    {
        Assert.Equal("jeffrey", Report.Deployments[0].StartedBy);
        Assert.Equal(new DeploymentWait("sign-off", "Sign-off", new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), "demo approvers"), Report.Deployments[0].WaitsFor);
        Assert.Null(Report.Deployments[1].WaitsFor);
        Assert.Equal(
            new RecentDeployment("demo-ui", "uat", "2.4.43", DeploymentState.Succeeded, "succeeded", new DateTimeOffset(2026, 10, 9, 7, 0, 0, TimeSpan.Zero), "ai-ops", new Uri($"{Tasks}2")),
            Report.Recent[1]);
        Assert.Null(Report.Recent[2].StartedBy);
        var freeze = Report.Freezes[1];
        Assert.Equal(("release week", new DateTimeOffset(2026, 10, 10, 19, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.Zero), false), (freeze.Name, freeze.From, freeze.To, freeze.Active));
        Assert.Equal(["uat", "prod"], freeze.Environments);
        Assert.Equal(["demo-ui", "demo-system"], freeze.Projects);
        Assert.Empty(Report.Missing);
    }

    [Fact]
    public void AFileFromBeforeTheFieldsReadsAsItDidAndSaysNothingMore()
    {
        var older = DeploymentsReport.Parse(Older)!;

        Assert.Empty(older.Recent);
        Assert.Empty(older.Freezes);
        Assert.Empty(older.Missing);
        Assert.All(older.Deployments, deployment => Assert.Null(deployment.StartedBy));
        Assert.All(older.Deployments, deployment => Assert.Null(deployment.WaitsFor));

        // The marks are what they were: the same sentences, the same tooltips, and no detail.
        Assert.All(Environments, environment =>
        {
            var marks = older.Marks(environment, Slug, Ui, Now);
            Assert.All(marks, mark => Assert.Null(mark.Detail));
            Assert.All(marks, mark => Assert.DoesNotContain('\n', DeploymentText.Title(mark)));
            Assert.Empty(DeploymentActivity.OfEnvironment(older, environment, marks, Now, Utc));
        });
        Assert.Equal("demo-ui 2.4.43 waits for a sign-off in prod (2 h so far)", DeploymentText.Title(Assert.Single(older.Marks("prod", Slug, Ui, Now))));

        // No node says anything in words, also not about what is in flight: the dot alone, as before.
        Assert.False(older.SaysActivity);
        Assert.All(Environments, environment => Assert.Empty(Node(older, environment)));
        Assert.All(Environments, environment => Assert.Empty(Node(older, environment, "system")));
    }

    [Theory]
    [InlineData("\"recent\": []")]
    [InlineData("\"freezes\": []")]
    [InlineData("\"missing\": [ \"freezes\" ]")]
    public void AFileWithOneOfTheNewListsSaysWhatIsInFlightInWordsWithWhatItHas(string list)
    {
        var report = DeploymentsReport.Parse(Older.TrimEnd()[..^1] + $", {list} }}")!;

        Assert.True(report.SaysActivity);
        Assert.Equal(new ActivityLine("waiting", "2.4.43 waits for a sign-off, 2 h", "demo-ui 2.4.43 waits for a sign-off in prod", new Uri($"{Tasks}5")), Assert.Single(Node(report, "prod")));
        Assert.Equal(new ActivityLine("deploying", "deploying 2.4.44, 3 min", "deploying demo-ui 2.4.44 to uat", new Uri($"{Tasks}4")), Assert.Single(Node(report, "uat")));
        Assert.Empty(Node(report, "tdd"));
    }

    [Theory]
    [InlineData("\"recent\": null")]
    [InlineData("\"missing\": \"nothing\"")]
    public void AListThatIsNoListDoesNotMakeAnOlderFileANewOne(string list) =>
        Assert.False(DeploymentsReport.Parse(Older.TrimEnd()[..^1] + $", {list} }}")!.SaysActivity);

    [Theory]
    [InlineData("""{ "recent": "none", "freezes": 3, "missing": { } }""")]
    [InlineData("""{ "recent": [ 1, "x", { "project": "demo-ui" } ], "freezes": [ { "name": "no end" }, { "to": "2026-10-12T00:00:00Z" } ], "missing": [ 1, null ] }""")]
    public void PartsThatAreNotWhatTheyShouldBeAreLeftOut(string rest)
    {
        var report = DeploymentsReport.Parse($$"""{ "deployments": [], {{rest[1..]}}""")!;

        Assert.Empty(report.Recent);
        Assert.Empty(report.Freezes);
        Assert.Empty(report.Missing);
    }

    [Fact]
    public void WhatTheSystemCouldNotReadIsKeptInItsWords()
    {
        var report = DeploymentsReport.Parse("""{ "deployments": [], "missing": [ "freezes", "startedBy" ] }""")!;

        Assert.Equal(["freezes", "startedBy"], report.Missing);
        Assert.Empty(Node(report, "uat"));
        Assert.Empty(Environment(report, "uat"));
    }

    [Fact]
    public void WhatADeploymentWaitsForIsReadOnlyWhileItWaits()
    {
        var report = Parse("""
            { "project": "demo-ui", "environment": "uat", "release": "1", "state": "executing", "waitsFor": { "kind": "sign-off" } },
            { "project": "demo-ui", "environment": "uat", "release": "2", "state": "waiting", "waitsFor": { "title": "no kind" } },
            { "project": "demo-ui", "environment": "uat", "release": "3", "state": "waiting", "waitsFor": "a sign-off" },
            { "project": "demo-ui", "environment": "uat", "release": "4", "state": "waiting", "waitsFor": { "kind": "sign-off", "responsible": "", "since": "soon" } }
            """);

        Assert.Equal([null, null, null, new DeploymentWait("sign-off", null, null, null)], report.Deployments.Select(deployment => deployment.WaitsFor));
    }

    [Fact]
    public void AMarkSaysAfterItsSentenceWhoIsResponsibleSinceWhenAndWhoStartedIt()
    {
        var prod = Assert.Single(Report.Marks("prod", Slug, Ui, Now));
        Assert.Equal("demo-ui 2.4.43 waits for a sign-off in prod", prod.Sentence);
        Assert.Equal("the sign-off is with demo approvers; asked 2 h ago; started by jeffrey", prod.Detail);
        Assert.Equal("demo-ui 2.4.43 waits for a sign-off in prod (2 h so far)\nthe sign-off is with demo approvers; asked 2 h ago; started by jeffrey", DeploymentText.Title(prod));

        var uat = Report.Marks("uat", Slug, Ui, Now);
        Assert.Equal(["started by ai-ops", "started by ai-ops"], uat.Select(mark => mark.Detail));
        Assert.Equal("deploying demo-ui 2.4.44 to uat (3 min so far)\nstarted by ai-ops", DeploymentText.Title(uat[0]));
    }

    [Theory]
    [InlineData("""{ "kind": "sign-off", "title": "Approve the release to production", "since": "2026-10-09T11:00:00Z", "responsible": "pat" }""",
        "demo-ui 2.4.43 waits for a sign-off in prod", "the sign-off \"Approve the release to production\" is with pat; asked 1 h ago")]
    [InlineData("""{ "kind": "sign-off", "title": "Approve the release to production" }""",
        "demo-ui 2.4.43 waits for a sign-off in prod", "the sign-off \"Approve the release to production\"")]
    [InlineData("""{ "kind": "sign-off", "title": "Sign-off", "responsible": "" }""", "demo-ui 2.4.43 waits for a sign-off in prod", null)]
    [InlineData("""{ "kind": "guided failure", "title": "Deploy ui failed", "since": "2026-10-09T11:50:00Z", "responsible": "demo approvers" }""",
        "demo-ui 2.4.43 asks what to do after a failed step in prod", "the question after a failed step \"Deploy ui failed\" is with demo approvers; asked 10 min ago")]
    [InlineData("""{ "kind": "review", "responsible": "pat" }""", "demo-ui 2.4.43 waits for a sign-off in prod", "review is with pat")]
    public void WhatItWaitsForIsSaidInWords(string waitsFor, string sentence, string? detail)
    {
        var mark = Assert.Single(Parse(Waiting(waitsFor)).Marks("prod", Slug, Ui, Now));

        Assert.Equal((sentence, detail), (mark.Sentence, mark.Detail));
    }

    [Fact]
    public void ANodeSaysTheMostUrgentFirstAndTwoLinesAtMost()
    {
        // prod: a sign-off is waited for, a freeze is in force, another comes, and something was deployed long ago.
        Assert.Equal(
            [
                new ActivityLine("waiting", "2.4.43 waits for demo approvers", "demo-ui 2.4.43 waits for a sign-off in prod (the sign-off is with demo approvers; asked 2 h ago; started by jeffrey)", new Uri($"{Tasks}5")),
                new ActivityLine(
                    "frozen",
                    "frozen until Mon 00:00: prod weekend",
                    "Deployment freeze prod weekend: no deployment of demo-ui to prod from 2026-10-09 00:00 +00:00 until 2026-10-12 00:00 +00:00. In force now. A freeze belongs to the Octopus Deploy instance; a person with the right may override it for one deployment."),
            ],
            Node(Report, "prod"));

        // uat: deploying, a freeze ahead, and the last one that ended; the freeze is more urgent than what is over.
        Assert.Equal(
            [("deploying", "deploying 2.4.44, 3 min, by ai-ops"), ("freeze", "freeze from Sat 19:00: release week")],
            Node(Report, "uat").Select(line => (line.Kind, line.Text)));

        // The system's own project is a deployable like another when a node is drawn for it.
        Assert.Equal(
            [("freeze", "freeze from Sat 19:00: release week"), ("failed", "1.0.7 failed 5 min ago by ai-ops")],
            Node(Report, "uat", "system").Select(line => (line.Kind, line.Text)));
        Assert.Equal(DeploymentActivity.MostLines, Node(Report, "prod").Count);
    }

    [Fact]
    public void ANodeWithNothingInFlightSaysTheLastThingThatHappened()
    {
        var report = Parse(recent: $$"""
            { "project": "demo-ui", "release": "2.4.43", "environment": "uat", "result": "succeeded", "finished": "2026-10-09T07:00:00Z", "startedBy": "ai-ops", "url": "{{Tasks}}2" },
            { "project": "demo-ui", "release": "2.4.40", "environment": "prod", "result": "canceled", "finished": "2026-10-01T15:00:00Z" },
            { "project": "demo-api", "release": "0.9.0", "environment": "uat", "result": "timed out", "finished": "2026-10-09T11:00:00Z" }
            """);

        Assert.Equal(
            new ActivityLine(
                "deployed",
                "2.4.43 deployed 5 h ago by ai-ops",
                "The last deployment of demo-ui to uat that ended: demo-ui 2.4.43 reached uat 5 h ago (2026-10-09 07:00 +00:00), started by ai-ops.",
                new Uri($"{Tasks}2")),
            Assert.Single(Node(report, "uat")));
        Assert.Equal(
            new ActivityLine("canceled", "2.4.40 canceled 7 d ago", "The last deployment of demo-ui to prod that ended: demo-ui 2.4.40 was canceled in prod 7 d ago (2026-10-01 15:00 +00:00)."),
            Assert.Single(Node(report, "prod")));

        // A result the page does not know is said in the file's word, with the quiet icon.
        Assert.Equal(("canceled", "0.9.0 timed out 1 h ago"), Assert.Single(Node(report, "UAT", "API").Select(line => (line.Kind, line.Text))));

        // Nothing where the file has nothing: another deployable, another environment.
        Assert.Empty(Node(report, "uat", "dashboard"));
        Assert.Empty(Node(report, "tdd"));
    }

    [Fact]
    public void OfTheLastDeploymentsOfANodeTheNewestIsSaidWhateverTheFilesOrder()
    {
        var report = Parse(recent: """
            { "project": "demo-ui", "release": "2.4.41", "environment": "uat", "result": "succeeded", "finished": "2026-10-09T05:00:00Z" },
            { "project": "demo-ui", "release": "2.4.42", "environment": "uat", "result": "failed", "finished": "2026-10-09T09:00:00Z" }
            """);

        Assert.Equal(("failed", "2.4.42 failed 3 h ago"), Assert.Single(Node(report, "uat").Select(line => (line.Kind, line.Text))));
    }

    [Theory]
    [InlineData("queued", "", "queued", "2.4.43 queued, 2 h")]
    [InlineData("executing", "", "deploying", "deploying 2.4.43, 2 h")]
    [InlineData("executing", """, "startedBy": "a-person-with-a-very-long-name" """, "deploying", "deploying 2.4.43, 2 h, by a-person-…")]
    [InlineData("paused", "", "queued", "2.4.43: paused")]
    public void WhatIsInFlightIsSaidShort(string state, string more, string kind, string text)
    {
        var report = Parse($$"""{ "project": "demo-ui", "environment": "prod", "release": "2.4.43", "state": "{{state}}", "since": "2026-10-09T09:58:00Z"{{more}} }""");

        var line = Assert.Single(Node(report, "prod"));
        Assert.Equal((kind, text), (line.Kind, line.Text));
        Assert.True(line.Text.Length <= DeploymentActivity.NodeLength);
    }

    [Theory]
    [InlineData("""{ "kind": "sign-off", "since": "2026-10-09T11:00:00Z" }""", "2.4.43 waits for a sign-off, 1 h")]
    [InlineData("""{ "kind": "sign-off", "responsible": "pat" }""", "2.4.43 waits for pat")]
    [InlineData("""{ "kind": "guided failure", "responsible": "pat" }""", "2.4.43 failed a step: pat decides")]
    [InlineData("""{ "kind": "guided failure" }""", "2.4.43 failed a step and waits")]
    [InlineData("""{ "kind": "sign-off", "responsible": "demo approvers, Space Managers, Octopus Administrators" }""", "2.4.43 waits for demo approvers, Sp…")]
    public void WhatWaitsForAPersonSaysWhoOnTheNode(string waitsFor, string text)
    {
        var line = Assert.Single(Node(Parse(Waiting(waitsFor)), "prod"));

        Assert.Equal(("waiting", text), (line.Kind, line.Text));
        Assert.True(line.Text.Length <= DeploymentActivity.NodeLength);
    }

    [Fact]
    public void OfSeveralDeploymentsInFlightTheOneAPersonHasToActOnIsFirst()
    {
        var report = Parse("""
            { "project": "demo-ui", "environment": "uat", "release": "3", "state": "queued", "since": "2026-10-09T11:59:00Z" },
            { "project": "demo-ui", "environment": "uat", "release": "2", "state": "executing", "since": "2026-10-09T11:58:00Z" },
            { "project": "demo-ui", "environment": "uat", "release": "1", "state": "waiting", "since": "2026-10-09T11:00:00Z" }
            """);

        Assert.Equal(["1 waits for a sign-off, 1 h", "deploying 2, 2 min"], Node(report, "uat").Select(line => line.Text));
    }

    [Fact]
    public void AFreezeIsInForceComingOrOverByTheBrowsersClock()
    {
        const string Freeze = """{ "name": "weekend", "from": "2026-10-10T19:00:00Z", "to": "2026-10-11T19:00:00Z", "active": false, "environments": [ "PROD" ], "projects": [ "Demo-UI" ] }""";
        var report = Parse(freezes: Freeze);
        (string Kind, string Text)? At(DateTimeOffset now) => Node(report, "prod", now: now).Select(line => ((string, string)?)(line.Kind, line.Text)).SingleOrDefault();

        // More than 72 hours ahead it is not named; then it comes, is in force (the file still says it is not), and is over.
        Assert.Null(At(new DateTimeOffset(2026, 10, 7, 18, 59, 0, TimeSpan.Zero)));
        Assert.Equal(("freeze", "freeze from Sat 19:00: weekend"), At(new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.Zero)));
        Assert.Equal(("freeze", "freeze from 19:00: weekend"), At(new DateTimeOffset(2026, 10, 10, 18, 59, 0, TimeSpan.Zero)));
        Assert.Equal(("frozen", "frozen until Sun 19:00: weekend"), At(new DateTimeOffset(2026, 10, 10, 19, 0, 0, TimeSpan.Zero)));
        Assert.Equal(("frozen", "frozen until 19:00: weekend"), At(new DateTimeOffset(2026, 10, 11, 18, 59, 0, TimeSpan.Zero)));
        Assert.Null(At(new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.Zero)));

        // It covers what it names and nothing else.
        Assert.Empty(Node(report, "uat"));
        Assert.Empty(Node(report, "prod", "dashboard"));

        // Without a start the file's own word decides.
        Assert.Equal("frozen", Assert.Single(Node(Parse(freezes: """{ "name": "x", "to": "2026-10-11T19:00:00Z", "active": true, "environments": [ "prod" ], "projects": [ "demo-ui" ] }"""), "prod")).Kind);
        Assert.Empty(Node(Parse(freezes: """{ "name": "x", "to": "2026-10-11T19:00:00Z", "active": false, "environments": [ "prod" ], "projects": [ "demo-ui" ] }"""), "prod"));
    }

    [Fact]
    public void ATimeNearNowIsShortAndInTheViewersZone()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var monday = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("Mon 00:00", DeploymentActivity.When(monday, Now, Utc));
        Assert.Equal("Sun 19:00", DeploymentActivity.When(monday, Now, chicago));
        Assert.Equal("19:00", DeploymentActivity.When(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), Now, chicago));
        Assert.Equal("25 Oct 00:00", DeploymentActivity.When(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), Now, Utc));
    }

    [Fact]
    public void UnderAnEnvironmentsNameTheFreezeAndTheLastDeploymentFollowTheMarks()
    {
        // prod: the mark of the sign-off is above; the freeze in force is named, with how many more; and the last one.
        var prod = Environment(Report, "prod");
        Assert.Equal(
            [
                ("frozen", "deployment freeze prod weekend: demo-ui frozen until Mon 00:00, and 1 more", (Uri?)null),
                ("deployed", "last: demo-ui 2.4.40 reached prod 7 d ago", new Uri($"{Tasks}1")),
            ],
            prod.Select(line => (line.Kind, line.Text, line.Url)));
        Assert.Equal(
            "Deployment freeze prod weekend: no deployment of demo-ui to prod from 2026-10-09 00:00 +00:00 until 2026-10-12 00:00 +00:00. In force now. A freeze belongs to the Octopus Deploy instance; a person with the right may override it for one deployment.\n"
            + "Deployment freeze release week: no deployment of demo-ui, demo-system to uat, prod from 2026-10-10 19:00 +00:00 until 2026-10-11 19:00 +00:00. Begins in 31 h. A freeze belongs to the Octopus Deploy instance; a person with the right may override it for one deployment.",
            prod[0].Title);

        // uat: the freeze that comes; the last deployment that ended is the one a mark already says, so it is not said twice.
        Assert.Equal(
            [("freeze", "deployment freeze release week: demo-ui, demo-system frozen from Sat 19:00 until Sun 19:00")],
            Environment(Report, "uat").Select(line => (line.Kind, line.Text)));

        // Ten minutes after it ended the mark is gone, and the line says it.
        Assert.Equal(
            ["deployment freeze release week: demo-ui, demo-system frozen from Sat 19:00 until Sun 19:00", "last: demo-system 1.0.7 failed in uat 15 min ago, started by ai-ops"],
            Environment(Report, "uat", Now.AddMinutes(10)).Select(line => line.Text));
        Assert.Equal("failed", Environment(Report, "uat", Now.AddMinutes(10))[1].Kind);
        Assert.Empty(Environment(Report, "tdd"));
    }

    [Fact]
    public void TheKindsAreInTheOrderOfTheirUrgency() =>
        Assert.Equal(["waiting", "deploying", "queued", "frozen", "freeze", "deployed", "failed", "canceled"], ActivityKind.All);

    [Fact]
    public void ATileOfTheDiagramCarriesTheActivityAsItsLastLinesAndInItsTitle()
    {
        var manifest = new RuntimeManifest(
            "prod",
            [
                new RuntimeNode("browser", RuntimeNodeKind.Person, "Browser"),
                new RuntimeNode("site_ui", RuntimeNodeKind.Other, "ui-prod", null, "ui"),
                new RuntimeNode("site_api", RuntimeNodeKind.Other, "api-prod", null, "api"),
                new RuntimeNode("dep_ui_mail", RuntimeNodeKind.Dependency, "Mail", null, "ui"),
            ],
            [],
            []);
        var marks = Report.Marks("prod", Slug, RuntimePayloadBuilder.Deployables(manifest), Now);

        var payload = RuntimePayloadBuilder.Build(manifest, null, null, Utc, marks, deployable => DeploymentActivity.OfNode(Report, "prod", Slug, deployable, Now, Utc));
        var tile = payload.Nodes.Single(node => node.Alias == "site_ui");

        // After the tile's own lines (here none), each a link to its task where the file has one; the tone is the kind.
        const string Waits = "demo-ui 2.4.43 waits for a sign-off in prod (the sign-off is with demo approvers; asked 2 h ago; started by jeffrey)";
        Assert.Equal(
            [
                new RuntimeTileLine(
                    "2.4.43 waits for demo approvers",
                    "waiting",
                    [new RuntimeTextPart("2.4.43 waits for demo approvers", new RuntimeLink($"{Tasks}5", $"{Waits}\n{TaskTitle}"))]),
                new RuntimeTileLine("frozen until Mon 00:00: prod weekend", "frozen"),
            ],
            tile.Lines,
            (expected, actual) => expected.Text == actual.Text && expected.Tone == actual.Tone
                && (expected.Parts ?? []).SequenceEqual(actual.Parts ?? []));

        // The dot is as it was; the title has the mark's words once, and the freeze's.
        Assert.Equal("waiting", tile.Deployment!.State);
        Assert.Equal(
            "ui-prod\ndemo-ui 2.4.43 waits for a sign-off in prod (2 h so far)\nthe sign-off is with demo approvers; asked 2 h ago; started by jeffrey\n"
            + "Deployment freeze prod weekend: no deployment of demo-ui to prod from 2026-10-09 00:00 +00:00 until 2026-10-12 00:00 +00:00. In force now. A freeze belongs to the Octopus Deploy instance; a person with the right may override it for one deployment.",
            tile.Title);

        // A deployable the file says nothing about, a dependency and the browser are as without the file.
        var without = RuntimePayloadBuilder.Build(manifest, null, null, Utc);
        Assert.All(Untouched, alias =>
        {
            var (before, after) = (without.Nodes.Single(node => node.Alias == alias), payload.Nodes.Single(node => node.Alias == alias));
            Assert.Equal(before.Lines, after.Lines);
            Assert.Equal(before with { Lines = [] }, after with { Lines = [] });
        });

        using var json = System.Text.Json.JsonDocument.Parse(payload.ToJson());
        var drawn = json.RootElement.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("alias").GetString() == "site_ui").GetProperty("lines");
        Assert.Equal(["waiting", "frozen"], drawn.EnumerateArray().Select(line => line.GetProperty("tone").GetString()));
        Assert.True(drawn[0].TryGetProperty("parts", out _));
        Assert.False(drawn[1].TryGetProperty("parts", out _));
    }

    [Fact]
    public void ANodeWithNoMarkSaysTheLastDeploymentInItsTitleToo()
    {
        var manifest = new RuntimeManifest("uat", [new RuntimeNode("site_ui", RuntimeNodeKind.Other, "ui-uat", null, "ui")], [], []);
        var report = Parse(recent: """{ "project": "demo-ui", "release": "2.4.43", "environment": "uat", "result": "failed", "finished": "2026-10-09T07:00:00Z" }""");

        var tile = Assert.Single(RuntimePayloadBuilder.Build(manifest, null, null, Utc, [], deployable => DeploymentActivity.OfNode(report, "uat", Slug, deployable, Now, Utc)).Nodes);

        Assert.Null(tile.Deployment);
        Assert.Equal(new RuntimeTileLine("2.4.43 failed 5 h ago", "failed"), Assert.Single(tile.Lines));
        Assert.Equal("ui-uat\nThe last deployment of demo-ui to uat that ended: demo-ui 2.4.43 failed in uat 5 h ago (2026-10-09 07:00 +00:00).", tile.Title);
    }

    [Fact]
    public void WithAFileFromBeforeTheFieldsThePayloadIsWhatItWasToTheLetter()
    {
        var older = DeploymentsReport.Parse(Older)!;
        Assert.All(Environments, environment =>
        {
            var manifest = new RuntimeManifest(
                environment,
                [new RuntimeNode("site_ui", RuntimeNodeKind.Other, "ui", null, "ui"), new RuntimeNode("site_system", RuntimeNodeKind.Other, "system", null, "system")],
                [],
                []);
            var marks = older.Marks(environment, Slug, RuntimePayloadBuilder.Deployables(manifest), Now);

            // Also where something is in flight (uat, prod): the dot, its title, and not a line more.
            Assert.Equal(
                RuntimePayloadBuilder.Build(manifest, null, null, Utc, marks).ToJson(),
                RuntimePayloadBuilder.Build(manifest, null, null, Utc, marks, deployable => DeploymentActivity.OfNode(older, environment, Slug, deployable, Now, Utc)).ToJson());
        });
    }

    [Fact]
    public async Task TheMonitorSaysTheActivityOfANodeAndOfAnEnvironment()
    {
        var time = new SignallingTimeProvider();
        var topology = Optics.Topology.Replace("\"deliveryUrl\"", $"\"deploymentsUrl\": \"https://raw.example.net{DeploymentsPath}\", \"deliveryUrl\"", StringComparison.Ordinal);
        var monitor = Optics.Monitor(
            new StubHandler(request => request.RequestUri!.AbsolutePath == DeploymentsPath ? StubHandler.Answer(HttpStatusCode.OK, Sample) : Optics.Answer(request)),
            time,
            new EventLog(),
            topology);

        // Before the file is read there is nothing to say.
        Assert.Empty(monitor.NodeActivity("prod", "ui", Now, Utc));
        Assert.Empty(monitor.EnvironmentActivity("prod", [], Now, Utc));

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(["waiting", "frozen"], monitor.NodeActivity("prod", "ui", Now, Utc).Select(line => line.Kind));
        var marks = monitor.DeploymentMarks("prod", Ui, Now);
        Assert.Equal(["frozen", "deployed"], monitor.EnvironmentActivity("prod", marks, Now, Utc).Select(line => line.Kind));
        Assert.Equal("All 4 nodes healthy", monitor.Summary.Text);
    }
}
