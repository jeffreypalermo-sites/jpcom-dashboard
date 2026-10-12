namespace Dashboard.Tests;

public class AvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 1, 30, 0, TimeSpan.Zero);

    private const string Delivery = """
        { "generated": "2026-10-07T01:00:00Z",
          "environments": [
            { "name": "tdd", "deployables": [ { "name": "ui", "version": "2.4.15" } ],
              "health": { "last24Hours": { "reports": 24, "healthy": 24 }, "last7Days": { "reports": 168, "healthy": 168 }, "lastFailure": null } },
            { "name": "prod", "deployables": [],
              "health": { "last24Hours": { "reports": 24, "healthy": 23 }, "last7Days": { "reports": 166, "healthy": 164 }, "lastFailure": "2026-10-06T16:13:00Z" } },
            { "name": "uat", "deployables": [ { "name": "ui", "version": "2.4.14" } ], "health": null },
            { "name": "dev", "deployables": [ { "name": "ui", "version": "2.4.14" } ] } ] }
        """;

    private static readonly DeliveryReport Report = DeliveryReport.Parse(Delivery)!;

    private static HealthReports Reports(string health) =>
        DeliveryReport.Parse($$"""{ "environments": [ { "name": "prod", "health": {{health}} } ] }""")!.HealthOf("prod")!;

    [Fact]
    public void TheHealthReportsOfAnEnvironmentAreRead()
    {
        Assert.Equal(
            new HealthReports(new HealthWindow(24, 23), new HealthWindow(166, 164), new DateTimeOffset(2026, 10, 6, 16, 13, 0, TimeSpan.Zero)),
            Report.HealthOf("PROD"));
        Assert.Equal(new HealthReports(new HealthWindow(24, 24), new HealthWindow(168, 168), null), Report.HealthOf("tdd"));
    }

    [Fact]
    public void AnEnvironmentWithoutThemHasNoneAndKeepsItsDeliveries()
    {
        Assert.Null(Report.HealthOf("uat"));
        Assert.Null(Report.HealthOf("dev"));
        Assert.Null(Report.HealthOf("staging"));
        Assert.Equal("2.4.14", Report.Find("uat", "ui")!.Version);
        Assert.Null(DeliveryReport.Parse(Optics.Delivery)!.HealthOf("uat"));
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "lastFailure": "2026-10-06T16:13:00Z" }""")]
    [InlineData("""{ "last24Hours": { "reports": 24 } }""")]
    [InlineData("""{ "last24Hours": { "reports": 24, "healthy": 25 } }""")]
    [InlineData("""{ "last24Hours": { "reports": -1, "healthy": 0 } }""")]
    [InlineData(""" "healthy" """)]
    public void CountsThatAreNotThereOrCannotBeAreNoReports(string health)
    {
        Assert.Null(DeliveryReport.Parse($$"""{ "environments": [ { "name": "prod", "health": {{health}} } ], "failover": { "environment": "uat" } }""")!.HealthOf("prod"));
    }

    [Fact]
    public void TheLineCountsTheHourlyChecksOfADayAndAWeekAndNamesTheLastFailure()
    {
        Assert.Equal(
            "Healthy in 23 of 24 hourly checks (95.8 %) in 24 hours · 164 of 166 in 7 days · last failure 9 h ago",
            AvailabilityText.Line(Report.HealthOf("prod")!, Now));
        Assert.Equal(
            "Healthy in 24 of 24 hourly checks (100 %) in 24 hours · 168 of 168 in 7 days · no failure in 7 days",
            AvailabilityText.Line(Report.HealthOf("tdd")!, Now));
    }

    [Fact]
    public void ThePartsCarryTheCountTheMomentAndThePercentOfTheWeek()
    {
        var parts = AvailabilityText.Parts(Report.HealthOf("prod")!, Now);

        Assert.Equal(
            [
                new AvailabilityPart("Healthy in ", "23 of 24", " hourly checks (95.8 %) in 24 hours"),
                new AvailabilityPart(string.Empty, "164 of 166", " in 7 days", "98.8 % of the hourly checks of the last 7 days"),
                new AvailabilityPart("last failure ", "9 h ago", string.Empty, When: new DateTimeOffset(2026, 10, 6, 16, 13, 0, TimeSpan.Zero)),
            ],
            parts);
    }

    [Fact]
    public void APartTheFileDoesNotHaveIsLeftOut()
    {
        Assert.Equal(
            "Healthy in 160 of 167 hourly checks (95.8 %) in 7 days",
            AvailabilityText.Line(Reports("""{ "last7Days": { "reports": 167, "healthy": 160 } }"""), Now));
        Assert.Equal(
            "Healthy in 1 of 1 hourly check (100 %) in 24 hours",
            AvailabilityText.Line(Reports("""{ "last24Hours": { "reports": 1, "healthy": 1 } }"""), Now));
        Assert.Empty(AvailabilityText.Parts(new HealthReports(null, null, null), Now));
    }

    [Fact]
    public void AWindowWithoutAReportSaysSoAndClaimsNothing()
    {
        Assert.Equal(
            "No hourly check ended in 24 hours · none in 7 days",
            AvailabilityText.Line(Reports("""{ "last24Hours": { "reports": 0, "healthy": 0 }, "last7Days": { "reports": 0, "healthy": 0 } }"""), Now));
        Assert.Equal(
            "No hourly check ended in 24 hours · 30 of 31 in 7 days · last failure 4 d ago",
            AvailabilityText.Line(Reports("""{ "last24Hours": { "reports": 0, "healthy": 0 }, "last7Days": { "reports": 31, "healthy": 30 }, "lastFailure": "2026-10-03T00:00:00Z" }"""), Now));
        Assert.Equal("No hourly check ended in 7 days", AvailabilityText.Line(Reports("""{ "last7Days": { "reports": 0, "healthy": 0 } }"""), Now));
    }

    [Theory]
    [InlineData(24, 24, "100 %")]
    [InlineData(24, 23, "95.8 %")]
    [InlineData(24, 12, "50 %")]
    [InlineData(24, 0, "0 %")]
    [InlineData(5000, 4999, "99.9 %")]
    [InlineData(5000, 1, "0.1 %")]
    [InlineData(0, 0, "–")]
    public void ThePercentHasOneDecimalAndIsNeverRoundedToAllOrNone(int reports, int healthy, string expected)
    {
        Assert.Equal(expected, AvailabilityText.Percent(new HealthWindow(reports, healthy)));
    }

    private static readonly DateTimeOffset Reported = new(2026, 10, 7, 1, 2, 13, TimeSpan.Zero);

    private static IReadOnlyList<ContainerAppReports>? ContainerApps(string containerApps) =>
        DeliveryReport.Parse($$"""
            { "environments": [ { "name": "tdd", "deployables": [],
                "health": { "last24Hours": { "reports": 24, "healthy": 23 }, "last7Days": { "reports": 168, "healthy": 166 }, "lastFailure": null,
                            "containerApps": {{containerApps}} } } ] }
            """)!.ContainerAppsOf("TDD");

    [Fact]
    public void WhatTheReportsFoundForEachContainerAppIsRead()
    {
        var apps = ContainerApps("""
            [ { "name": "ui", "last": { "result": "healthy", "at": "2026-10-07T01:02:13Z" }, "last24Hours": { "reports": 24, "healthy": 24 }, "last7Days": { "reports": 168, "healthy": 167 } },
              { "name": "scorecard", "last": { "result": "Unreachable", "at": "2026-10-07T01:02:13Z" }, "last24Hours": null, "last7Days": { "reports": 70, "healthy": 69 } },
              { "name": "worker", "last": null, "last24Hours": { "reports": 0, "healthy": 0 }, "last7Days": { "reports": 0, "healthy": 0 } },
              { "last": { "result": "healthy", "at": "2026-10-07T01:02:13Z" } },
              "ui" ]
            """);

        Assert.Equal(
            [
                new ContainerAppReports("ui", new HourlyCheck(HourlyCheck.Healthy, Reported), new HealthWindow(24, 24), new HealthWindow(168, 167)),
                new ContainerAppReports("scorecard", new HourlyCheck(HourlyCheck.Unreachable, Reported), null, new HealthWindow(70, 69)),
                new ContainerAppReports("worker", null, new HealthWindow(0, 0), new HealthWindow(0, 0)),
            ],
            apps!);
    }

    [Theory]
    [InlineData("""{ "result": "sleeping", "at": "2026-10-07T01:02:13Z" }""")]
    [InlineData("""{ "result": "healthy" }""")]
    [InlineData("""{ "result": "healthy", "at": "an hour ago" }""")]
    [InlineData("""{ "at": "2026-10-07T01:02:13Z" }""")]
    [InlineData("\"healthy\"")]
    public void AResultIsOneOfThreeWordsWithItsTimeOrNone(string last) =>
        Assert.Null(Assert.Single(ContainerApps($$"""[ { "name": "ui", "last": {{last}} } ]""")!).Last);

    [Theory]
    [InlineData("""{ "reports": 24, "healthy": 25 }""")]
    [InlineData("""{ "reports": 24 }""")]
    [InlineData("null")]
    public void ACountThatIsNotThereOrCannotBeIsNone(string window) =>
        Assert.Null(Assert.Single(ContainerApps($$"""[ { "name": "ui", "last24Hours": {{window}} } ]""")!).Last24Hours);

    [Fact]
    public void AnEnvironmentWithoutThemHasNoneAndAnEmptyListIsNoContainerApp()
    {
        Assert.Null(Report.ContainerAppsOf("prod"));
        Assert.Null(Report.ContainerAppsOf("uat"));
        Assert.Null(Report.ContainerAppsOf("staging"));
        Assert.Null(ContainerApps("null"));
        Assert.Null(ContainerApps("""{ "ui": "healthy" }"""));
        Assert.Empty(ContainerApps("[]")!);
    }

    [Fact]
    public void TheBoxSaysTheResultOfTheLastTwoHoursAndTheCountsInWords()
    {
        var ui = new ContainerAppReports("ui", new HourlyCheck(HourlyCheck.Unhealthy, Reported), new HealthWindow(24, 23), new HealthWindow(0, 0));

        Assert.Equal(TimeSpan.FromHours(2), HourlyCheckText.Recent);
        Assert.Equal("hourly: unhealthy, 27 min ago", HourlyCheckText.Result(ui, Now));
        Assert.Equal("hourly: unhealthy, 2 h ago", HourlyCheckText.Result(ui, Reported.AddHours(2)));
        Assert.Equal(HourlyCheckText.NoRecentCheck, HourlyCheckText.Result(ui, Reported.AddHours(2).AddSeconds(1)));
        Assert.Equal("no recent hourly check", HourlyCheckText.Result(null, Now));
        Assert.Equal("24 h: 23 of 24 · 7 d: none", HourlyCheckText.Counts(ui));
        Assert.Null(HourlyCheckText.Counts(ui with { Last24Hours = null, Last7Days = null }));
        Assert.Null(HourlyCheckText.Counts(null));
    }

    [Fact]
    public void TheTooltipSaysTheseAreHourlyChecksOfThePipelineAndWhenTheFactsChanged()
    {
        var help = AvailabilityText.Help(Report.Generated, TimeZoneInfo.Utc);

        Assert.Contains("once an hour the runbook \"Health report\" in Octopus Deploy asks every node", help, StringComparison.Ordinal);
        Assert.Contains("It is no continuous monitoring: an outage between two reports is not counted.", help, StringComparison.Ordinal);
        Assert.EndsWith("As of the last change of the delivery facts: 2026-10-07 01:00 +00:00.", help, StringComparison.Ordinal);
        Assert.DoesNotContain("As of", AvailabilityText.Help(null, TimeZoneInfo.Utc), StringComparison.Ordinal);
        Assert.Equal("Hourly checks by the pipeline, not continuous monitoring.", AvailabilityText.Note);
    }
}
