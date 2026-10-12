using System.Net;

namespace Dashboard.Tests;

public class ApplicationViewTests
{
    private const string Base = "https://raw.githubusercontent.com/org/app/architecture/ui";

    private const string ArchitectureJson = """
        {
          "deployable": "ui",
          "version": "1.0.42",
          "generated": "2026-10-12T15:37:48Z",
          "kind": "container image",
          "process": "dotnet Ui.Server.dll",
          "entry": "Ui.Server",
          "assemblies": [
            { "name": "Ui.Server", "entry": true, "references": ["Core", "DataAccess"],
              "packages": [ { "name": "Serilog", "version": "4.0.1" }, { "name": "MediatR" } ] },
            { "name": "Core", "entry": false, "references": [], "packages": [] },
            { "name": "DataAccess", "references": ["Core"] }
          ],
          "truncated": false,
          "later": { "ignored": true }
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);

    private static readonly Topology SampleTopology =
        TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;

    private static readonly RuntimeManifest Sample = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;

    /// <summary>The sample manifest of uat with the architecture of ui on both of its web apps, as the deployment writes it.</summary>
    private static readonly RuntimeManifest WithArchitecture = Sample with
    {
        Nodes = [.. Sample.Nodes.Select(node => node.Kind == RuntimeNodeKind.WebApp ? node with { Architecture = new Uri(Base) } : node)],
    };

    private static RuntimeNode Node(string node) =>
        Assert.Single(RuntimeManifestParser.ParseManifest($$"""{ "environment": "tdd", "nodes": [ {{node}} ] }""").Value!.Nodes);

    private static EnvironmentStatus Uat() => new(SampleTopology.Environments.Single(environment => environment.Name == "uat"));

    private static ProbeResult Answer(string? version) => new(HealthState.Healthy, 200, 40, Now, version);

    [Theory]
    [InlineData("webapp", RuntimeNodeKind.WebApp)]
    [InlineData("containerapp", RuntimeNodeKind.ContainerApp)]
    public void AWebAppOrAContainerAppMayNameTheArchitectureOfItsDeployable(string kind, RuntimeNodeKind read)
    {
        var node = Node($$"""{ "alias": "app_ui", "kind": "{{kind}}", "deployable": "ui", "architecture": "{{Base}}" }""");

        Assert.Equal(read, node.Kind);
        Assert.Equal(new Uri(Base), node.Architecture);
    }

    [Fact]
    public void ANodeWithoutAnArchitectureHasNoneAndTheSampleHasNone()
    {
        Assert.Null(Node("""{ "alias": "app_ui", "kind": "webapp", "deployable": "ui" }""").Architecture);
        Assert.All(Sample.Nodes, node => Assert.Null(node.Architecture));
        Assert.Empty(ApplicationText.Boxes(Sample));
    }

    [Theory]
    [InlineData("\"http://raw.githubusercontent.com/org/app/architecture/ui\"")]
    [InlineData("\"architecture/ui\"")]
    [InlineData("\"//raw.githubusercontent.com/org/app/architecture/ui\"")]
    [InlineData("\"javascript:alert(1)\"")]
    [InlineData("\"https://raw.githubusercontent.com/org/app/architecture/ui?x=1\"")]
    [InlineData("\"https://user@raw.githubusercontent.com/org/app/architecture/ui\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{ }")]
    public void AnArchitectureThatIsNoAbsoluteHttpsAddressIsLeftOutAndTheManifestIsRead(string architecture)
    {
        var manifest = RuntimeManifestParser.ParseManifest($$"""
            { "environment": "tdd", "nodes": [ { "alias": "app_ui", "kind": "webapp", "deployable": "ui", "architecture": {{architecture}} } ] }
            """);

        Assert.True(manifest.IsValid, string.Join(' ', manifest.Errors));
        Assert.Null(Assert.Single(manifest.Value!.Nodes).Architecture);
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("frontdoor")]
    [InlineData("staticsite")]
    [InlineData("resource")]
    [InlineData("dependency")]
    [InlineData("something-new")]
    public void AnArchitectureOnAnotherKindOfNodeIsLeftOut(string kind)
    {
        Assert.Null(Node($$"""{ "alias": "a", "kind": "{{kind}}", "deployable": "ui", "architecture": "{{Base}}" }""").Architecture);
    }

    [Fact]
    public void TheBoxesOfADeployableWithAnArchitectureOpenItsAssembliesThePrimaryFirst()
    {
        var boxes = ApplicationText.Boxes(WithArchitecture, "UI");

        Assert.Equal(["app_ui_primary", "app_ui_standby"], boxes.Select(box => box.Alias));
        Assert.Equal("Application ui: open its assemblies", ApplicationText.BoxLabel(boxes[0]));
        Assert.Equal(["app_ui_standby", "app_ui_primary"], ApplicationText.Boxes(WithArchitecture).Select(box => box.Alias));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("ui<script>")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownDeployableOrARefusedNameHasNoBox(string? deployable)
    {
        Assert.Empty(ApplicationText.Boxes(WithArchitecture, deployable));
    }

    [Fact]
    public void ANodeWhoseDeployablesNameTheAddressRefusesIsNoBox()
    {
        var manifest = Sample with { Nodes = [new RuntimeNode("app", RuntimeNodeKind.WebApp, "app", Deployable: "ui/../x", Architecture: new Uri(Base))] };

        Assert.Empty(ApplicationText.Boxes(manifest));
    }

    [Theory]
    [InlineData("runtime/uat/app/ui", "uat", "ui")]
    [InlineData("#runtime/uat/app/ui/", "uat", "ui")]
    [InlineData("Runtime/uat/App/Work-Order_api2", "uat", "Work-Order_api2")]
    [InlineData("runtime/uat/app/database", "uat", "database")]
    [InlineData("runtime/app/app/ui", "app", "ui")]
    public void TheHashNamesTheApplicationViewOfADeployable(string hash, string environment, string application)
    {
        Assert.Equal(new ViewAddress(DashboardView.Runtime, environment, Application: application), ViewAddress.Parse(hash));
    }

    [Theory]
    [InlineData("runtime/uat/app/<script>alert(1)<script>")]
    [InlineData("runtime/uat/app/ui.server")]
    [InlineData("runtime/uat/app/ui%20x")]
    [InlineData("runtime/uat/app/ui x")]
    [InlineData("runtime/uat/app/..")]
    [InlineData("runtime/uat/app/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AHostileDeployableNameNamesNoViewAndTheEnvironmentsDiagramIsShown(string hash)
    {
        Assert.Equal(new ViewAddress(DashboardView.Runtime, "uat"), ViewAddress.Parse(hash));
    }

    [Fact]
    public void TheDatabaseViewAndAnEnvironmentCalledAppKeepTheirAddresses()
    {
        Assert.Equal(new ViewAddress(DashboardView.Runtime, "uat", Database: true), ViewAddress.Parse("runtime/uat/database"));
        Assert.Equal(new ViewAddress(DashboardView.Runtime, "app", Database: true), ViewAddress.Parse("runtime/app/database"));
        Assert.Equal(new ViewAddress(DashboardView.Runtime, "app"), ViewAddress.Parse("runtime/app"));
        Assert.Equal(new ViewAddress(DashboardView.Runtime, "app/ui"), ViewAddress.Parse("runtime/app/ui"));
    }

    [Fact]
    public void TheApplicationViewWritesTheHashThatOpensIt()
    {
        var address = new ViewAddress(DashboardView.Runtime, "uat", Application: "ui");

        Assert.Equal("runtime/uat/app/ui", address.ToHash());
        Assert.Equal(address, ViewAddress.Parse(address.ToHash()));

        // Without an environment there is no application to show, and a refused name is never written.
        Assert.Equal("runtime", new ViewAddress(DashboardView.Runtime, null, Application: "ui").ToHash());
        Assert.Equal("runtime/uat", new ViewAddress(DashboardView.Runtime, "uat", Application: "ui/x").ToHash());
        Assert.Equal("runtime/uat/database", new ViewAddress(DashboardView.Runtime, "uat", Database: true).ToHash());
    }

    [Fact]
    public void TheVersionIsTheOneThatRunsBeforeThePinnedOne()
    {
        Assert.Equal(new ApplicationVersion("1.0.42", Runs: true), ApplicationText.Version([("primary", "1.0.42")], "1.0.41"));
        Assert.Equal(new ApplicationVersion("1.0.41", Runs: false), ApplicationText.Version([("primary", null)], "1.0.41"));
        Assert.Equal(new ApplicationVersion("1.0.41", Runs: false), ApplicationText.Version([], " 1.0.41 "));
    }

    [Fact]
    public void WithoutARunningAndWithoutAPinnedVersionNoneIsKnown()
    {
        Assert.Null(ApplicationText.Version([("primary", null), ("standby", " ")], null));
        Assert.Null(ApplicationText.Version([], ""));
    }

    [Fact]
    public void NodesThatRunDifferentVersionsShowThePrimarysElseTheFirstKnown()
    {
        Assert.Equal("1.0.42", ApplicationText.Version([("standby", "1.0.41"), ("Primary", "1.0.42")], null)!.Text);
        Assert.Equal("1.0.41", ApplicationText.Version([("standby", "1.0.41"), ("primary", null)], "1.0.40")!.Text);
        Assert.Equal("1.0.7", ApplicationText.Version([(null, null), (null, "1.0.7"), (null, "1.0.8")], null)!.Text);
    }

    [Fact]
    public void TheVersionOfADiagramsBoxesIsWhatTheirNodesReportedAndElseThePin()
    {
        var boxes = ApplicationText.Boxes(WithArchitecture, "ui");
        var uat = Uat();
        Assert.Null(ApplicationText.Version(boxes, uat));
        Assert.Null(ApplicationText.Version(boxes, null));

        uat.Record(PinnedVersions.Parse("""{ "ui": "1.0.40" }"""));
        Assert.Equal(new ApplicationVersion("1.0.40", Runs: false), ApplicationText.Version(boxes, uat));

        uat.Deployables[0].Nodes.Single(node => !node.IsPrimary).Record(Answer("1.0.41"));
        Assert.Equal(new ApplicationVersion("1.0.41", Runs: true), ApplicationText.Version(boxes, uat));

        uat.Deployables[0].Nodes.Single(node => node.IsPrimary).Record(Answer("1.0.42"));
        Assert.Equal(new ApplicationVersion("1.0.42", Runs: true), ApplicationText.Version(boxes, uat));
    }

    [Fact]
    public void ADeployableTheTopologyDoesNotListShowsThePinnedVersion()
    {
        var manifest = Sample with { Nodes = [new RuntimeNode("ca_api", RuntimeNodeKind.ContainerApp, "ca-api", Deployable: "api", Architecture: new Uri(Base))] };
        var uat = Uat();
        uat.Record(PinnedVersions.Parse("""{ "api": "3.1.0" }"""));

        Assert.Equal(new ApplicationVersion("3.1.0", Runs: false), ApplicationText.Version(ApplicationText.Boxes(manifest, "api"), uat));
    }

    [Fact]
    public void TheVersionLineSaysWhichVersionItIs()
    {
        Assert.Equal("Version 1.0.42, as it runs", ApplicationText.VersionLine("ui", new ApplicationVersion("1.0.42", Runs: true)));
        Assert.Equal("Version 1.0.42, as pinned", ApplicationText.VersionLine("ui", new ApplicationVersion("1.0.42", Runs: false)));
        Assert.Equal("The version of ui is not known yet.", ApplicationText.VersionLine("ui", null));
    }

    [Theory]
    [InlineData("1.0.42", "https://raw.githubusercontent.com/org/app/architecture/ui/1.0.42.svg")]
    [InlineData("1.0.42-beta.1", "https://raw.githubusercontent.com/org/app/architecture/ui/1.0.42-beta.1.svg")]
    [InlineData("1.0.42+build_7", "https://raw.githubusercontent.com/org/app/architecture/ui/1.0.42%2Bbuild_7.svg")]
    public void TheFilesOfAVersionAreBuiltFromTheArchitectureAndTheVersion(string version, string address)
    {
        Assert.Equal(address, ApplicationText.File(new Uri(Base), version, "svg")!.AbsoluteUri);
        Assert.Equal(address, ApplicationText.File(new Uri(Base + "/"), version, "svg")!.AbsoluteUri);
        Assert.Equal(address[..^3] + "json", ApplicationText.File(new Uri(Base), version, "json")!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("../../../../evil/main/x")]
    [InlineData("1.0.42/x")]
    [InlineData("1.0.42?x=1")]
    [InlineData("1.0.42#x")]
    [InlineData("1.0 42")]
    [InlineData("1.0.42\" onerror=\"alert(1)")]
    [InlineData("1.0.42%2f")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void AVersionThatIsNoPlainVersionNamesNoFile(string? version)
    {
        Assert.False(ApplicationText.IsVersion(version));
        Assert.Null(ApplicationText.File(new Uri(Base), version, "svg"));
        Assert.False(ApplicationRefresh.IsDue(null, version));
    }

    [Fact]
    public void TheArchitectureOfAVersionIsRead()
    {
        var architecture = ApplicationParser.Parse(ArchitectureJson)!;

        Assert.Equal("container image", architecture.Kind);
        Assert.Equal("dotnet Ui.Server.dll", architecture.Process);
        Assert.Equal("Ui.Server", architecture.Entry);
        Assert.False(architecture.Truncated);
        Assert.Equal(["Ui.Server", "Core", "DataAccess"], architecture.Assemblies.Select(assembly => assembly.Name));
        Assert.Equal([true, false, false], architecture.Assemblies.Select(assembly => assembly.Entry));
        Assert.Equal(["Core", "DataAccess"], architecture.Assemblies[0].References);
        Assert.Equal(["Serilog 4.0.1", "MediatR"], architecture.Assemblies[0].Packages.Select(ApplicationText.Package));
        Assert.Equal(["Core"], architecture.Assemblies[2].References);
        Assert.Empty(architecture.Assemblies[2].Packages);
        Assert.Equal("container image, process: dotnet Ui.Server.dll", ApplicationText.KindLine(architecture));
        Assert.Null(ApplicationText.TruncatedLine(architecture));
        Assert.Equal("Assemblies of ui 1.0.42: 3 assemblies, entry Ui.Server", ApplicationText.AltText("ui", "1.0.42", architecture));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("404: Not Found")]
    [InlineData("<!DOCTYPE html><html></html>")]
    public void AMalformedFileIsNoFile(string? json)
    {
        Assert.Null(ApplicationParser.Parse(json));
    }

    [Fact]
    public void PartsOfAnotherTypeAreLeftOut()
    {
        var architecture = ApplicationParser.Parse("""
            {
              "kind": 7, "process": "", "entry": ["Ui.Server"], "truncated": "yes",
              "assemblies": [
                "Ui.Server",
                { "name": 4 },
                { "name": "Core", "entry": "true", "references": "DataAccess", "packages": { "name": "Serilog" } },
                { "name": "DataAccess", "references": [ "Core", 4, "", null, { } ], "packages": [ "Serilog", { "version": "1.0.0" }, { "name": "Dapper", "version": 2 } ] }
              ]
            }
            """)!;

        Assert.Null(architecture.Kind);
        Assert.Null(architecture.Process);
        Assert.Null(architecture.Entry);
        Assert.False(architecture.Truncated);
        Assert.Equal(["Core", "DataAccess"], architecture.Assemblies.Select(assembly => assembly.Name));
        Assert.All(architecture.Assemblies, assembly => Assert.False(assembly.Entry));
        Assert.Empty(architecture.Assemblies[0].References);
        Assert.Empty(architecture.Assemblies[0].Packages);
        Assert.Equal(["Core"], architecture.Assemblies[1].References);
        Assert.Equal([new ApplicationPackage("Dapper", null)], architecture.Assemblies[1].Packages);
        Assert.Null(ApplicationText.KindLine(architecture));
    }

    [Fact]
    public void AnAssemblyNamedAsTheEntryIsTheEntryWhereItDoesNotSay()
    {
        var architecture = ApplicationParser.Parse("""{ "entry": "Ui.Server", "assemblies": [ { "name": "Ui.Server" }, { "name": "Core" } ] }""")!;

        Assert.Equal([true, false], architecture.Assemblies.Select(assembly => assembly.Entry));
    }

    [Fact]
    public void ATruncatedFileSaysSo()
    {
        var architecture = ApplicationParser.Parse("""{ "truncated": true, "assemblies": [ { "name": "Ui.Server" } ] }""")!;

        Assert.True(architecture.Truncated);
        Assert.Equal("The diagram and the list show only a part of the assemblies.", ApplicationText.TruncatedLine(architecture));
        Assert.Equal("Assemblies of ui 1.0.42: 1 assembly", ApplicationText.AltText("ui", "1.0.42", architecture));
    }

    [Fact]
    public void AContainerThatIsNotDotNetShowsItsProcessAlone()
    {
        var architecture = ApplicationParser.Parse("""{ "kind": "container image", "process": "node server.js", "entry": "", "assemblies": [] }""")!;

        Assert.Empty(architecture.Assemblies);
        Assert.Equal("container image, process: node server.js", ApplicationText.KindLine(architecture));
        Assert.Equal("Architecture of api 2.0.0: no assemblies, process node server.js", ApplicationText.AltText("api", "2.0.0", architecture));
        Assert.Equal("process: sh", ApplicationText.KindLine(architecture with { Kind = null, Process = "sh" }));
        Assert.Equal("container image", ApplicationText.KindLine(architecture with { Process = null }));
    }

    [Fact]
    public void AVeryLongWordOfTheFileIsCut()
    {
        var architecture = ApplicationParser.Parse($$"""{ "process": "{{new string('x', 5000)}}" }""")!;

        Assert.Equal(300, architecture.Process!.Length);
    }

    [Fact]
    public void AVersionWithoutADiagramAndOneThatCouldNotBeReadSayItInOneLine()
    {
        Assert.Equal(
            "No architecture diagram of ui 1.0.42 yet: its repository publishes one with each release.",
            ApplicationText.ProblemLine("ui", "1.0.42", ApplicationReadState.Missing));
        Assert.Equal("The architecture diagram of ui 1.0.42 could not be read.", ApplicationText.ProblemLine("ui", "1.0.42", ApplicationReadState.Failed));
        Assert.Null(ApplicationText.ProblemLine("ui", "1.0.42", ApplicationReadState.Read));
    }

    [Fact]
    public void TheFileOfAVersionIsAskedForOnceAndAgainOnlyForAnotherVersion()
    {
        // The view was just opened: nothing was asked.
        Assert.True(ApplicationRefresh.IsDue(null, "1.0.42"));

        // Every check round of the page renders the view again with the same version.
        Assert.False(ApplicationRefresh.IsDue("1.0.42", "1.0.42"));

        Assert.True(ApplicationRefresh.IsDue("1.0.42", "1.0.43"));
        Assert.False(ApplicationRefresh.IsDue("1.0.42", null));
        Assert.False(ApplicationRefresh.IsDue(null, null));
    }

    private static RuntimeLoader Loader(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dashboard.example.net/") });

    [Fact]
    public async Task TheArchitectureIsReadFromTheRepositorysAddress()
    {
        var file = ApplicationText.File(new Uri(Base), "1.0.42", "json")!;
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, ArchitectureJson));

        var read = await Loader(handler).ReadArchitectureAsync(file, CancellationToken.None);

        Assert.Equal(ApplicationReadState.Read, read.State);
        Assert.Equal("Ui.Server", read.Architecture!.Entry);
        Assert.Equal([$"{Base}/1.0.42.json"], handler.RequestedUrls);
    }

    [Fact]
    public async Task AVersionTheRepositoryPublishedNoDiagramOfIsMissingAndAnyOtherFailureIsNotRead()
    {
        var file = ApplicationText.File(new Uri(Base), "1.0.42", "json")!;

        Assert.Same(ApplicationRead.Missing, await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found"))).ReadArchitectureAsync(file, CancellationToken.None));
        Assert.Same(ApplicationRead.Failed, await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.InternalServerError))).ReadArchitectureAsync(file, CancellationToken.None));
        Assert.Same(ApplicationRead.Failed, await Loader(new StubHandler(_ => throw new HttpRequestException("CORS"))).ReadArchitectureAsync(file, CancellationToken.None));
        Assert.Same(ApplicationRead.Failed, await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, "<html>"))).ReadArchitectureAsync(file, CancellationToken.None));
    }
}
