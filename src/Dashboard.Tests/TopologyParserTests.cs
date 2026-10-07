namespace Dashboard.Tests;

public class TopologyParserTests
{
    private static Topology Valid(string json)
    {
        var result = TopologyParser.Parse(json);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Errors);
        return result.Topology!;
    }

    private static IReadOnlyList<string> Invalid(string? json)
    {
        var result = TopologyParser.Parse(json);
        Assert.False(result.IsValid);
        Assert.Null(result.Topology);
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    [Fact]
    public void TheSampleShippedWithTheAppIsRead()
    {
        var topology = Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json")));

        Assert.Equal(new SystemInfo("cmdemo2", "CM demo 2 multi-region"), topology.System);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 22, 0, 0, TimeSpan.Zero), topology.Generated);
        Assert.Equal(["tdd", "uat"], topology.Environments.Select(environment => environment.Name));
        Assert.All(topology.Environments, environment => Assert.Equal("nonprod", environment.Tier));

        var tdd = Assert.Single(topology.Environments[0].Deployables);
        Assert.Equal("ui", tdd.Name);
        Assert.Equal(new Uri("https://cmdemo2-tdd-abc123.z01.azurefd.net"), tdd.FrontDoor);
        Assert.Equal("/_healthcheck", tdd.HealthPath);
        Assert.Equal("/alive", tdd.AlivePath);
        Assert.Equal("/_version", tdd.VersionPath);
        Assert.Equal(
            new NodeInfo("app-cmdemo2-tdd-ui", "westus3", "primary", new Uri("https://app-cmdemo2-tdd-ui.azurewebsites.net")),
            Assert.Single(tdd.Nodes));

        var uat = Assert.Single(topology.Environments[1].Deployables);
        Assert.Equal(["westus3", "eastus2"], uat.Nodes.Select(node => node.Region));
        Assert.Equal(["primary", "standby"], uat.Nodes.Select(node => node.Role));
        Assert.True(uat.Nodes[0].IsPrimary);
        Assert.False(uat.Nodes[1].IsPrimary);
    }

    [Fact]
    public void TheProbeDecidesThePath()
    {
        var deployable = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [
              { "healthPath": "/health", "alivePath": "/live", "nodes": [ { "url": "https://a.example.net" } ] } ] } ] }
            """).Environments[0].Deployables[0];

        Assert.Equal("/health", deployable.PathFor(ProbeKind.Health));
        Assert.Equal("/live", deployable.PathFor(ProbeKind.Liveness));
    }

    [Theory]
    [InlineData("""{ "name": "ui", "frontDoor": null, "nodes": [] }""")]
    [InlineData("""{ "name": "ui", "nodes": [] }""")]
    public void FrontDoorMayBeNullOrAbsent(string deployable)
    {
        var topology = Valid($$"""{ "environments": [ { "name": "tdd", "deployables": [ {{deployable}} ] } ] }""");

        Assert.Null(topology.Environments[0].Deployables[0].FrontDoor);
    }

    [Fact]
    public void MissingOptionalFieldsGetDefaults()
    {
        var topology = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [ { "nodes": [
              { "url": "https://one.example.net" },
              { "url": "https://two.example.net/" } ] } ] } ] }
            """);

        Assert.Equal(new SystemInfo(string.Empty, "System"), topology.System);
        Assert.Null(topology.Generated);
        Assert.Null(topology.Environments[0].Tier);

        var deployable = topology.Environments[0].Deployables[0];
        Assert.Equal("app", deployable.Name);
        Assert.Equal("/_healthcheck", deployable.HealthPath);
        Assert.Equal("/alive", deployable.AlivePath);
        Assert.Equal("/_version", deployable.VersionPath);

        // Without a name the host names the node; without a role the first node is the primary.
        Assert.Equal(["one.example.net", "two.example.net"], deployable.Nodes.Select(node => node.Name));
        Assert.Equal(["primary", "standby"], deployable.Nodes.Select(node => node.Role));
        Assert.All(deployable.Nodes, node => Assert.Null(node.Region));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var topology = Valid("""
            {
              "schema": 7, "owner": { "team": "platform" },
              "system": { "slug": "demo", "name": "Demo", "colour": "blue" },
              "environments": [ { "name": "prod", "tier": "prod", "order": 3, "deployables": [
                { "name": "ui", "sku": "P1v3", "nodes": [
                  { "url": "https://a.example.net", "role": "primary", "zone": [1, 2, 3] } ] } ] } ]
            }
            """);

        Assert.Equal("Demo", topology.System.Name);
        Assert.Equal("prod", topology.Environments[0].Tier);
    }

    [Fact]
    public void OptionalFieldsOfTheWrongTypeFallBackToTheDefault()
    {
        var topology = Valid("""
            { "system": "demo", "generated": 12, "environments": [ { "name": "tdd", "tier": 1, "deployables": [
              { "name": 5, "healthPath": true, "nodes": [ { "url": "https://a.example.net", "region": 3, "role": [] } ] } ] } ] }
            """);

        var deployable = topology.Environments[0].Deployables[0];
        Assert.Equal("System", topology.System.Name);
        Assert.Null(topology.Generated);
        Assert.Equal("app", deployable.Name);
        Assert.Equal("/_healthcheck", deployable.HealthPath);
        Assert.Equal("primary", deployable.Nodes[0].Role);
    }

    [Fact]
    public void TheSlugNamesASystemWithoutAName() =>
        Assert.Equal("demo", Valid("""{ "system": { "slug": "demo" }, "environments": [] }""").System.Name);

    [Fact]
    public void AnEnvironmentWithoutDeployablesAndADeployableWithoutNodesAreEmpty()
    {
        var topology = Valid("""{ "environments": [ { "name": "tdd" }, { "name": "uat", "deployables": [ { "name": "ui" } ] } ] }""");

        Assert.Empty(topology.Environments[0].Deployables);
        Assert.Empty(topology.Environments[1].Deployables[0].Nodes);
    }

    [Fact]
    public void APathWithoutLeadingSlashGetsOne()
    {
        var deployable = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [ { "healthPath": "health", "nodes": [] } ] } ] }
            """).Environments[0].Deployables[0];

        Assert.Equal("/health", deployable.HealthPath);
    }

    [Fact]
    public void RoleIsReadWithoutRegardToCase()
    {
        var node = Valid("""
            { "environments": [ { "name": "tdd", "deployables": [ { "nodes": [
              { "url": "https://a.example.net", "role": "Standby" }, { "url": "https://b.example.net", "role": "PRIMARY" } ] } ] } ] }
            """).Environments[0].Deployables[0].Nodes;

        Assert.False(node[0].IsPrimary);
        Assert.True(node[1].IsPrimary);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed() =>
        Assert.Empty(Valid("""
            {
              // written by hand
              "environments": [],
            }
            """).Environments);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyFileIsAnError(string? json) =>
        Assert.Equal("The file is empty.", Assert.Single(Invalid(json)));

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>", "The file is not valid JSON (line 1, position 1).")]
    [InlineData("{ \"environments\": [\n  { \"name\": }\n] }", "The file is not valid JSON (line 2, position 13).")]
    public void MalformedJsonIsAnErrorWithThePlace(string json, string expected) =>
        Assert.Equal(expected, Assert.Single(Invalid(json)));

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"topology\"")]
    public void AnythingButAnObjectIsAnError(string json) =>
        Assert.Equal("The file must contain a JSON object.", Assert.Single(Invalid(json)));

    [Theory]
    [InlineData("""{ "system": { "slug": "demo" } }""")]
    [InlineData("""{ "environments": null }""")]
    [InlineData("""{ "environments": { "tdd": {} } }""")]
    public void EnvironmentsMustBeAnArray(string json) =>
        Assert.Equal("environments: missing or not an array.", Assert.Single(Invalid(json)));

    [Fact]
    public void EveryProblemIsReportedWithItsPlace()
    {
        var errors = Invalid("""
            { "environments": [
              { "tier": "nonprod" },
              "uat",
              { "name": "prod", "deployables": [
                { "frontDoor": "not an address", "nodes": [
                  { "name": "no-url" },
                  { "url": "ftp://files.example.net" },
                  { "url": "/relative" },
                  7 ] },
                { "nodes": "none" } ] },
              { "name": "dr", "deployables": {} } ] }
            """);

        Assert.Equal(
            [
                "environments[0].name: missing or empty.",
                "environments[1]: not an object.",
                "environments[2].deployables[0].frontDoor: not an absolute http or https address.",
                "environments[2].deployables[0].nodes[0].url: missing or not an absolute http or https address.",
                "environments[2].deployables[0].nodes[1].url: missing or not an absolute http or https address.",
                "environments[2].deployables[0].nodes[2].url: missing or not an absolute http or https address.",
                "environments[2].deployables[0].nodes[3]: not an object.",
                "environments[2].deployables[1].nodes: not an array.",
                "environments[3].deployables: not an array.",
            ],
            errors);
    }
}
