namespace Dashboard.Tests;

/// <summary>The resources in no environment: the manifest's list <c>unplaced</c>, and the rows under the diagram.</summary>
public class UnplacedTests
{
    private static readonly DateTimeOffset Read = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private const string RegistryId = "/subscriptions/s/resourceGroups/rg-demo1-prod/providers/Microsoft.ContainerRegistry/registries/acrdemo1";

    private static RuntimeManifest Manifest(string unplaced) =>
        RuntimeManifestParser.ParseManifest($$"""{ "environment": "tdd", "nodes": [], "unplaced": {{unplaced}} }""").Value!;

    private static ResourceHealthReport Report(string id, string status, string source) =>
        ResourceHealthReport.Parse($$"""
            { "resources": { "prod": [ { "id": "{{id}}", "health": { "status": "{{status}}", "source": "{{source}}", "checkedAt": "2026-10-12T09:00:00Z" } } ] } }
            """)!;

    [Fact]
    public void AnEntryIsReadWithItsWordsItsIdsAndItsLinks()
    {
        var manifest = Manifest($$"""
            [ { "name": "acrdemo1", "type": "Container registry", "group": "rg-demo1-prod", "words": "no environment tag",
                "ids": ["{{RegistryId}}"],
                "url": "https://ca-x.example.azurecontainerapps.io",
                "links": { "portal": "https://portal.azure.com/#@t/resource{{RegistryId}}/overview", "site": "https://ca-x.example.io/" } } ]
            """);

        var entry = Assert.Single(manifest.Unplaced!);
        Assert.Equal("acrdemo1", entry.Name);
        Assert.Equal("Container registry", entry.Type);
        Assert.Equal("rg-demo1-prod", entry.Group);
        Assert.Equal("no environment tag", entry.Words);
        Assert.Equal([RegistryId], entry.Ids);
        Assert.Equal("portal.azure.com", entry.Portal!.Host);
        // links.site comes before url.
        Assert.Equal(new Uri("https://ca-x.example.io/"), entry.Site);
    }

    [Fact]
    public void TheAddressIsTheUrlWhereNoLinkNamesTheSite()
    {
        var entry = Assert.Single(Manifest("""[ { "name": "ca-x", "url": "https://ca-x.example.io" } ]""").Unplaced!);

        Assert.Equal(new Uri("https://ca-x.example.io"), entry.Site);
        Assert.Null(entry.Portal);
        Assert.Null(entry.Type);
        Assert.Null(entry.Ids);
    }

    [Fact]
    public void AManifestWithoutTheListHasNoneAndAnEmptyListIsEmpty()
    {
        Assert.Null(RuntimeManifestParser.ParseManifest("""{ "environment": "tdd", "nodes": [] }""").Value!.Unplaced);
        Assert.Null(RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!.Unplaced);
        Assert.Empty(Manifest("[]").Unplaced!);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"acrdemo1\"")]
    [InlineData("7")]
    [InlineData("""{ "name": "acrdemo1" }""")]
    public void AListOfTheWrongTypeIsLeftOutAndTheManifestIsRead(string unplaced)
    {
        var result = RuntimeManifestParser.ParseManifest($$"""{ "environment": "tdd", "nodes": [], "unplaced": {{unplaced}} }""");

        Assert.True(result.IsValid);
        Assert.Null(result.Value!.Unplaced);
    }

    [Fact]
    public void AnEntryWithoutANameIsSkippedAndAFieldOfTheWrongTypeIsLeftOut()
    {
        var manifest = Manifest("""
            [ 1, "x", null, [], {}, { "name": "" }, { "name": "   " }, { "name": 5 }, { "type": "Vault" },
              { "name": "kv-demo", "type": 3, "group": ["rg"], "words": { "a": 1 }, "ids": "/x", "url": 9, "links": "https://portal.azure.com" },
              { "name": "id-demo", "ids": [1, "", null, " /y "], "links": { "portal": 4, "site": null } } ]
            """);

        Assert.Equal(
            [new RuntimeUnplaced("kv-demo"), new RuntimeUnplaced("id-demo")],
            manifest.Unplaced!.Select(entry => entry with { Ids = null }));
        Assert.Null(manifest.Unplaced![0].Ids);
        Assert.Equal(["/y"], manifest.Unplaced[1].Ids);
    }

    [Fact]
    public void AHostileStringIsKeptAsTheTextItIs()
    {
        const string Markup = "<img src=x onerror=alert(1)>";
        var manifest = Manifest("""
            [ { "name": "<img src=x onerror=alert(1)>", "type": "<script>alert(1)</script>", "group": "\"><b>rg</b>", "words": "javascript:alert(1)" } ]
            """);

        var entry = Assert.Single(manifest.Unplaced!);
        Assert.Equal(Markup, entry.Name);
        Assert.Equal("<script>alert(1)</script>", entry.Type);
        Assert.Equal("\"><b>rg</b>", entry.Group);
        Assert.Equal("javascript:alert(1)", entry.Words);
        var row = Assert.Single(UnplacedText.Rows(manifest, null, Read, TimeZoneInfo.Utc));
        Assert.Equal(Markup, row.Entry.Name);
    }

    [Theory]
    [InlineData("http://portal.azure.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("//portal.azure.com/x")]
    [InlineData("/resource/x")]
    [InlineData("portal.azure.com")]
    [InlineData("https://user:secret@portal.azure.com/x")]
    [InlineData("ftp://portal.azure.com/x")]
    public void AnAddressThatIsNoAbsoluteHttpsAddressIsDropped(string address)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(address);
        var manifest = Manifest($$"""[ { "name": "acrdemo1", "url": {{json}}, "links": { "portal": {{json}}, "site": {{json}} } } ]""");

        var entry = Assert.Single(manifest.Unplaced!);
        Assert.Null(entry.Portal);
        Assert.Null(entry.Site);
    }

    [Fact]
    public void ALongTextIsCutAndALongListEndsAtItsCap()
    {
        var text = new string('x', 2 * RuntimeManifestParser.LongestUnplacedText);
        var entry = Assert.Single(Manifest($$"""[ { "name": "{{text}}", "type": "{{text}}", "group": "{{text}}", "words": "{{text}}" } ]""").Unplaced!);

        Assert.Equal(300, RuntimeManifestParser.LongestUnplacedText);
        Assert.All(new[] { entry.Name, entry.Type, entry.Group, entry.Words }, kept => Assert.Equal(300, kept!.Length));

        var many = string.Join(',', Enumerable.Range(0, RuntimeManifestParser.MostUnplaced + 40).Select(number => $$"""{ "name": "r{{number}}" }"""));
        var list = Manifest($"[{many}]").Unplaced!;

        Assert.Equal(500, list.Count);
        Assert.Equal("r0", list[0].Name);
        Assert.Equal("r499", list[^1].Name);
    }

    [Fact]
    public void ThePageAsksForResourceHealthWhereOnlyAnUnplacedEntryHasIds()
    {
        var bare = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;
        Assert.False(RuntimePayloadBuilder.Watch(bare).ResourceHealth);

        Assert.False(RuntimePayloadBuilder.Watch(bare with { Unplaced = [] }).ResourceHealth);
        Assert.False(RuntimePayloadBuilder.Watch(bare with { Unplaced = [new RuntimeUnplaced("acrdemo1"), new RuntimeUnplaced("kv", Ids: [])] }).ResourceHealth);

        var watch = RuntimePayloadBuilder.Watch(bare with { Unplaced = [new RuntimeUnplaced("acrdemo1", Ids: [RegistryId])] });
        Assert.True(watch.ResourceHealth);
        Assert.Empty(watch.Apps);
    }

    [Theory]
    [InlineData("available", "resourceHealth", "Available · Resource Health, 40 min ago")]
    [InlineData("degraded", "resourceHealth", "Degraded · Resource Health, 40 min ago")]
    [InlineData("unavailable", "resourceHealth", "Unavailable · Resource Health, 40 min ago")]
    [InlineData("succeeded", "provisioningState", "Provisioned · provisioning state, 40 min ago")]
    [InlineData("unknown", "none", "Not known · no Resource Health, 40 min ago")]
    public void ARowSaysWhatResourceHealthSaidInTheWordsOfABox(string status, string source, string expected)
    {
        var manifest = Manifest($$"""[ { "name": "acrdemo1", "ids": ["{{RegistryId}}"] } ]""");
        var now = Read.AddMinutes(40);

        var row = Assert.Single(UnplacedText.Rows(manifest, Report(RegistryId, status, source), now, TimeZoneInfo.Utc));
        var box = ResourceHealthText.Of([RegistryId], Report(RegistryId, status, source), now, TimeZoneInfo.Utc)!;

        Assert.Equal(expected, row.Health);
        Assert.Equal($"{box.Label} · {box.Line}", row.Health);
        Assert.Equal("plain", row.HealthTone);
        Assert.Equal(box.Title, row.HealthTitle);
        Assert.Contains(ResourceHealthText.Source, row.HealthTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWithoutAReadingSaysSoAndOneWithoutAnIdIsNotRead()
    {
        var manifest = Manifest($$"""
            [ { "name": "a-not-in-the-reading", "ids": ["/s/other"] }, { "name": "b-no-id" }, { "name": "c-old", "ids": ["{{RegistryId}}"] } ]
            """);
        var report = Report(RegistryId, "available", "resourceHealth");

        var rows = UnplacedText.Rows(manifest, report, Read.AddHours(5), TimeZoneInfo.Utc);

        Assert.Equal("Not known · not in the hourly reading", rows[0].Health);
        Assert.Equal("muted", rows[0].HealthTone);
        Assert.Equal(UnplacedText.NotRead, rows[1].Health);
        Assert.Equal("muted", rows[1].HealthTone);
        Assert.Equal(UnplacedText.NotReadTitle, rows[1].HealthTitle);
        Assert.StartsWith("Available · Resource Health, ", rows[2].Health, StringComparison.Ordinal);
        Assert.Equal("muted", rows[2].HealthTone);

        // Before the page read the file at all.
        var unread = UnplacedText.Rows(manifest, null, Read, TimeZoneInfo.Utc);
        Assert.Equal("Not known · no Resource Health read yet", unread[0].Health);
        Assert.Equal(UnplacedText.NotRead, unread[1].Health);
    }

    [Fact]
    public void TheRowsAreInTheOrderOfTheirTypeAndThenTheirName()
    {
        var manifest = Manifest("""
            [ { "name": "kv-b", "type": "Key vault" }, { "name": "untyped" }, { "name": "acr-z", "type": "Container registry" },
              { "name": "KV-a", "type": "key vault" }, { "name": "acr-a", "type": "Container registry" } ]
            """);

        var rows = UnplacedText.Rows(manifest, null, Read, TimeZoneInfo.Utc);

        Assert.Equal(["acr-a", "acr-z", "KV-a", "kv-b", "untyped"], rows.Select(row => row.Entry.Name));
        Assert.Equal("In no environment (5)", UnplacedText.Summary(rows.Count));
    }

    [Fact]
    public void AManifestWithoutTheListHasNoRows()
    {
        var old = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;

        Assert.Empty(UnplacedText.Rows(old, null, Read, TimeZoneInfo.Utc));
        Assert.Empty(UnplacedText.Rows(old with { Unplaced = [] }, null, Read, TimeZoneInfo.Utc));
    }
}
