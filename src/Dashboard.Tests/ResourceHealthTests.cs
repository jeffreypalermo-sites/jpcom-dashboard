namespace Dashboard.Tests;

public class ResourceHealthTests
{
    private static readonly DateTimeOffset Read = new(2026, 10, 4, 21, 37, 0, TimeSpan.Zero);

    [Fact]
    public void EveryRecordWithItsHealthIsFoundByItsIdWhateverItsCase()
    {
        var report = ResourceHealthReport.Parse("""
            { "generated": "2026-10-04T21:37:00Z",
              "environments": { "tdd": { "ui": "https://ca-demo-tdd-ui.example.io" } },
              "resources": {
                "nonprod": [
                  { "type": "Microsoft.KeyVault/vaults", "name": "kv-demo", "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv-demo",
                    "health": { "status": "Degraded", "source": "resourceHealth", "checkedAt": "2026-10-04T21:37:00Z", "summary": "We're sorry, your key vault is degraded." } },
                  { "type": "Microsoft.ManagedIdentity/userAssignedIdentities", "name": "id-demo", "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo",
                    "health": { "status": "succeeded", "source": "provisioningState", "checkedAt": "2026-10-04T21:37:00Z" } },
                  { "type": "Microsoft.Web/serverFarms", "name": "asp-old", "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Web/serverFarms/asp-old" }
                ],
                "apps": [
                  { "id": "/subscriptions/s/resourceGroups/apps/providers/Microsoft.App/containerApps/ca-x",
                    "health": { "status": "unknown", "source": "none", "checkedAt": "2026-10-04T21:37:00Z" } }
                ] } }
            """)!;

        Assert.Equal(Read, report.Generated);
        Assert.Equal(3, report.Readings.Count);
        Assert.Equal(
            new ResourceHealthReading("degraded", ResourceHealthReading.FromResourceHealth, Read, "We're sorry, your key vault is degraded."),
            report.Find("/SUBSCRIPTIONS/s/resourcegroups/RG/providers/microsoft.keyvault/vaults/KV-DEMO"));
        Assert.Equal(ResourceHealthReading.FromProvisioningState, report.Find("/subscriptions/s/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo")?.Source);
        Assert.Equal(ResourceHealthReading.None, report.Find("/subscriptions/s/resourceGroups/apps/providers/Microsoft.App/containerApps/ca-x")?.Source);
        Assert.Null(report.Find("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Web/serverFarms/asp-old"));
    }

    [Theory]
    [InlineData("""{ "status": "available", "source": "resourceHealth" }""")]
    [InlineData("""{ "status": "", "source": "resourceHealth", "checkedAt": "2026-10-04T21:37:00Z" }""")]
    [InlineData("""{ "status": "<b>up</b>", "source": "resourceHealth", "checkedAt": "2026-10-04T21:37:00Z" }""")]
    [InlineData("""{ "status": 1, "checkedAt": "2026-10-04T21:37:00Z" }""")]
    [InlineData("""null""")]
    public void AReadingWithoutAStatusOfWordsOrWithoutItsTimeIsLeftOut(string health)
    {
        var report = ResourceHealthReport.Parse($$"""{ "resources": { "nonprod": [ { "id": "/s/kv", "health": {{health}} } ] } }""")!;

        Assert.Empty(report.Readings);
    }

    [Fact]
    public void AnUnknownSourceIsNoneAndALongSummaryIsCut()
    {
        var reading = ResourceHealthReport.Parse($$"""
            { "resources": { "nonprod": [ { "id": "/s/kv", "health": { "status": "available", "source": "guess", "checkedAt": "2026-10-04T21:37:00Z", "summary": "{{new string('x', 500)}}" } } ] } }
            """)!.Find("/s/kv")!;

        Assert.Equal(ResourceHealthReading.None, reading.Source);
        Assert.Equal(200, reading.Summary!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{ "environments": {} }""")]
    [InlineData("""{ "resources": [] }""")]
    public void AFileWithoutResourcesIsNoReport(string json)
    {
        Assert.Null(ResourceHealthReport.Parse(json));
    }

    [Theory]
    [InlineData("available", "resourceHealth", "Available")]
    [InlineData("degraded", "resourceHealth", "Degraded")]
    [InlineData("unavailable", "resourceHealth", "Unavailable")]
    [InlineData("unknown", "resourceHealth", "Not known")]
    [InlineData("succeeded", "provisioningState", "Provisioned")]
    [InlineData("failed", "provisioningState", "Failed")]
    [InlineData("unknown", "none", "Not known")]
    public void EachReadingHasItsWord(string status, string source, string label)
    {
        Assert.Equal(label, ResourceHealthText.Label(new ResourceHealthReading(status, source, Read)));
    }

    [Fact]
    public void ABoxWithoutIdsHasNoWords()
    {
        Assert.Null(ResourceHealthText.Of(null, null, Read, TimeZoneInfo.Utc));
        Assert.Null(ResourceHealthText.Of([], null, Read, TimeZoneInfo.Utc));
    }

    [Fact]
    public void WithoutATimeTheLineHasNoAge()
    {
        var report = ResourceHealthReport.Parse("""{ "resources": { "g": [ { "id": "/s/kv", "health": { "status": "succeeded", "source": "provisioningState", "checkedAt": "2026-10-04T21:37:00Z" } } ] } }""");

        var words = ResourceHealthText.Of(["/s/kv"], report, null, TimeZoneInfo.Utc)!;

        Assert.Equal(("Provisioned", "provisioning state", "muted"), (words.Label, words.Line, words.Tone));
    }
}
