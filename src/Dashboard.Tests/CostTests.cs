using System.Net;

namespace Dashboard.Tests;

public class CostTests
{
    /// <summary>The day after the sample's <c>asOf</c>: its numbers are yesterday's.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly AsOf = new(2026, 10, 3);
    private readonly SignallingTimeProvider _time = new();

    private static readonly CostReport Report = CostReport.Parse(Optics.Cost)!;

    [Fact]
    public void TheCostFileIsRead()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 5, 0, 0, TimeSpan.Zero), Report.Generated);
        Assert.Equal("USD", Report.Currency);
        Assert.Equal(AsOf, Report.AsOf);
        Assert.Equal(new CostAmounts(3.41, 22.1, 1204.8), Report.System);
        Assert.Equal(["tdd", "uat", "retired", "shared"], Report.Entries.Select(entry => entry.Name));

        var uat = Report.Find("UAT")!;
        Assert.Equal(new CostAmounts(1.52, 9.8, 11.02), uat.Amounts);
        Assert.Equal([new CostService("Azure App Service", 6.1)], uat.TopServices);
        Assert.Equal([new CostService("SQL Database", 0.97), new CostService("Azure App Service", 0.62)], Report.Find("tdd")!.TopServices);
        Assert.Null(Report.Find("prod"));
    }

    [Fact]
    public void AnEnvironmentOfOneClusterHasAnEstimateOfItsPartOfWhatIsShared()
    {
        var report = CostReport.Parse("""
            { "currency": "USD", "asOf": "2026-10-03",
              "system": { "yesterday": 5.85, "last7Days": 13.44, "monthToDate": 13.44 },
              "environments": [
                { "name": "tdd", "yesterday": 0, "last7Days": 0, "monthToDate": 0,
                  "estimate": { "share": 0.2048, "yesterday": 1.17, "last7Days": 2.69, "monthToDate": 2.69 } },
                { "name": "uat", "monthToDate": 0.4, "estimate": { "monthToDate": 1.5 } },
                { "name": "prod", "monthToDate": 0.4, "estimate": { "share": 7, "yesterday": 1 } },
                { "name": "old", "monthToDate": 0.4, "estimate": { "share": 0.2 } },
                { "name": "shared", "yesterday": 5.85, "last7Days": 13.44, "monthToDate": 13.44 } ] }
            """)!;

        var tdd = report.Find("tdd")!;
        Assert.Equal(new CostEstimate(0.2048, new CostAmounts(1.17, 2.69, 2.69)), tdd.Estimate);
        Assert.Equal("plus about $2.69 this month of what the environments share (20 % of what all pods request)", CostText.Estimate(tdd.Estimate, report, Now));
        // Without a share the amount stands alone; a share that is none is left out; no month, no words; no amount, no estimate.
        Assert.Equal("plus about $1.50 this month of what the environments share", CostText.Estimate(report.Find("uat")!.Estimate, report, Now));
        Assert.Equal(new CostEstimate(null, new CostAmounts(1, null, null)), report.Find("prod")!.Estimate);
        Assert.Null(CostText.Estimate(report.Find("prod")!.Estimate, report, Now));
        Assert.Null(report.Find("old")!.Estimate);
        Assert.Null(report.Shared!.Estimate);
        Assert.Null(CostText.Estimate(null, report, Now));
        // The whole is still in the shared entry: an estimate takes nothing from it.
        Assert.Equal(new CostAmounts(5.85, 13.44, 13.44), report.Shared.Amounts);
    }

    [Fact]
    public void WhatNoEnvironmentOwnsIsTheSharedEntryAndWhatTheTopologyDoesNotKnowAreTheOthers()
    {
        Assert.Equal(new CostAmounts(1.1, 7.7, 8.2), Report.Shared!.Amounts);
        Assert.True(CostReport.IsShared("Shared"));
        Assert.Equal(["retired"], Report.Others(["TDD", "uat"]).Select(entry => entry.Name));
        Assert.Equal(["uat", "retired"], Report.Others(["tdd"]).Select(entry => entry.Name));
        Assert.Null(CostReport.Parse("""{ "environments": [ { "name": "tdd" } ] }""")!.Shared);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("404: Not Found")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("""{ "generated": "2026-10-04T05:00:00Z", "currency": "USD", "asOf": "2026-10-03", "system": null, "environments": "none" }""")]
    public void AnythingButCostIsNoReport(string? body) => Assert.Null(CostReport.Parse(body));

    [Fact]
    public void EveryPartIsOptionalAndANumberAzureDidNotGiveIsNull()
    {
        var report = CostReport.Parse(
            """
            { "asOf": "yesterday", "system": { "yesterday": null, "last7Days": "2", "monthToDate": -0.3 },
              "environments": [ { "name": "uat", "yesterday": null, "topServices": [ { "name": "SQL Database" }, { "monthToDate": 1 }, 7 ] }, { "yesterday": 1 }, 7 ] }
            """)!;

        Assert.Null(report.Generated);
        Assert.Null(report.Currency);
        Assert.Null(report.AsOf);
        Assert.Equal(new CostAmounts(null, null, -0.3), report.System);
        var uat = Assert.Single(report.Entries);
        Assert.Equal(new CostAmounts(null, null, null), uat.Amounts);
        Assert.Equal([new CostService("SQL Database", null)], uat.TopServices);
        Assert.Null(CostReport.Parse("""{ "system": { } }""")!.System!.MonthToDate);
        Assert.Empty(CostReport.Parse("""{ "system": { } }""")!.Entries);
    }

    [Theory]
    [InlineData(1.52, "USD", "$1.52")]
    [InlineData(9.8, "usd", "$9.80")]
    [InlineData(0d, "USD", "$0.00")]
    [InlineData(1204.8, "USD", "$1,204.80")]
    [InlineData(0.005, "USD", "$0.01")]
    [InlineData(-0.3, "USD", "-$0.30")]
    [InlineData(-0.001, "USD", "$0.00")]
    [InlineData(1.5, "EUR", "1.50 EUR")]
    [InlineData(1.5, null, "1.50")]
    [InlineData(null, "USD", "—")]
    [InlineData(null, null, "—")]
    public void MoneyIsSaidInItsCurrencyAndANumberThatIsNotThereIsADash(double? amount, string? currency, string expected) =>
        Assert.Equal(expected, CostText.Money(amount, currency));

    [Fact]
    public void TheLineNamesTheDaysItsNumbersCoverAndTheDayTheyAreOf()
    {
        Assert.Equal("$1.52 yesterday · $9.80 in 7 days · $11.02 this month · as of 2026-10-03", CostText.Line(Report.Find("uat")!.Amounts, Report, Now));
        Assert.Equal("$3.41 yesterday · $22.10 in 7 days · $1,204.80 this month · as of 2026-10-03", CostText.Line(Report.System!, Report, Now));
        Assert.Equal("— yesterday · $0.20 in 7 days · $0.20 this month · as of 2026-10-03", CostText.Line(Report.Find("retired")!.Amounts, Report, Now));
        Assert.Equal(
            [new CostPart("—", "yesterday"), new CostPart("—", "in 7 days"), new CostPart("—", "this month")],
            CostText.Parts(new CostAmounts(null, null, null), "USD", AsOf, Now));
    }

    [Fact]
    public void AFileThatWasNotRenewedIsNeverSaidToBeOfYesterdayOrOfThisMonth()
    {
        // Two days later the same file is of "2026-10-03", not of yesterday; in November it is of October.
        Assert.Equal("$1.52 on 2026-10-03 · $9.80 in 7 days · $11.02 this month · as of 2026-10-03", CostText.Line(Report.Find("uat")!.Amounts, Report, Now.AddDays(1)));
        Assert.Equal("on 2026-10-03", CostText.Day(AsOf, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("in October", CostText.Month(AsOf, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("in October 2026", CostText.Month(AsOf, new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void OnTheFirstOfAMonthTheNumbersAreOfTheMonthThatEnded()
    {
        var lastOfSeptember = new DateOnly(2026, 9, 30);
        var firstOfOctober = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);

        Assert.Equal("yesterday", CostText.Day(lastOfSeptember, firstOfOctober));
        Assert.Equal("in September", CostText.Month(lastOfSeptember, firstOfOctober));
    }

    [Fact]
    public void YesterdayIsTheUtcDayBeforeNowWhateverTheViewersOffset()
    {
        // 2026-10-04 22:00 UTC is already 2026-10-05 in Tokyo: the file's days are UTC days.
        Assert.Equal("yesterday", CostText.Day(AsOf, Now.ToOffset(TimeSpan.FromHours(9))));
        Assert.Equal("this month", CostText.Month(AsOf, Now.ToOffset(TimeSpan.FromHours(9))));
    }

    [Fact]
    public void WithoutItsDayTheLineSaysNoDate()
    {
        var undated = Report with { AsOf = null };

        Assert.Equal("$1.52 in the last full day · $9.80 in 7 days · $11.02 this month", CostText.Line(Report.Find("uat")!.Amounts, undated, Now));
        Assert.Null(CostText.AsOf(null));
        Assert.Equal("as of 2026-10-03", CostText.AsOf(AsOf));
    }

    [Fact]
    public void TheServicesThatCostMostAreListedWithTheirAmounts()
    {
        Assert.Equal("Most this month: SQL Database $0.97, Azure App Service $0.62", CostText.Services(Report.Find("tdd")!.TopServices, Report, Now));
        Assert.Equal("Most in October: Azure Front Door Service $8.11", CostText.Services(Report.Shared!.TopServices, Report, Now.AddMonths(1)));
        Assert.Equal("Most this month: SQL Database —", CostText.Services([new CostService("SQL Database", null)], Report, Now));
        Assert.Null(CostText.Services(Report.Find("retired")!.TopServices, Report, Now));
        Assert.Null(CostText.Services(null, Report, Now));
    }

    [Fact]
    public void TheHelpSaysTheNumbersAreNotLiveAndWhichDaysTheyAreOf()
    {
        Assert.StartsWith("Not live: what Azure Cost Management reported for complete UTC days up to 2026-10-03, by the tag \"environment\"", CostText.Help(Report), StringComparison.Ordinal);
        Assert.StartsWith("Not live: what Azure Cost Management reported for complete UTC days, by", CostText.Help(Report with { AsOf = null }), StringComparison.Ordinal);
        Assert.Contains("\"environment\" = \"retired\"", CostText.OtherHelp("retired"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTopologyCarriesTheCostAddressAndRefusesOneThatIsNoAddress()
    {
        Assert.Equal("https://raw.example.net/org/demo-system/status/cost.json", TopologyParser.Parse(Optics.TopologyWithCost).Topology!.System.CostUrl?.AbsoluteUri);
        Assert.Null(TopologyParser.Parse(Optics.Topology).Topology!.System.CostUrl);
        Assert.Null(TopologyParser.Parse("""{ "system": { "costUrl": null }, "environments": [] }""").Topology!.System.CostUrl);
        Assert.Equal(
            ["system.costUrl: not an absolute http or https address."],
            TopologyParser.Parse("""{ "system": { "costUrl": "cost.json" }, "environments": [] }""").Errors);
    }

    [Fact]
    public async Task TheFileIsReadWithTheFirstRoundAndThenEveryFiveMinutes()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time, topology: Optics.TopologyWithCost);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        Assert.Equal(11.02, monitor.Cost!.Find("uat")!.Amounts.MonthToDate);
        Assert.Equal("2.4.14", monitor.Delivery!.Find("uat", "ui")!.Version);

        for (var round = 0; round < 9; round++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        }

        Assert.Equal(1, Optics.Count(handler, Optics.CostPath));

        _time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, Optics.Count(handler, Optics.CostPath));
    }

    [Fact]
    public async Task AFileThatIsNotPublishedYetShowsNothingAndFailsNoCheck()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == Optics.CostPath ? StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time, topology: Optics.TopologyWithCost);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Cost);
        Assert.NotNull(monitor.Delivery);
        Assert.Equal("All 4 nodes healthy", monitor.Summary.Text);
    }

    [Fact]
    public async Task AFailedReadingKeepsTheLastGoodOne()
    {
        var offline = false;
        var handler = new StubHandler(request => offline && request.RequestUri!.AbsolutePath == Optics.CostPath ? throw new HttpRequestException("offline") : Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time, topology: Optics.TopologyWithCost);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);
        offline = true;
        _time.Advance(DashboardMonitor.CostInterval);
        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Equal(2, Optics.Count(handler, Optics.CostPath));
        Assert.Equal(AsOf, monitor.Cost!.AsOf);
    }

    [Fact]
    public async Task WithoutACostAddressNothingIsRead()
    {
        var handler = new StubHandler(request => Optics.Answer(request));
        var monitor = Optics.Monitor(handler, _time);

        await monitor.CheckAllAsync(ProbeKind.Health, CancellationToken.None);

        Assert.Null(monitor.Cost);
        Assert.Equal(0, Optics.Count(handler, Optics.CostPath));
    }
}
