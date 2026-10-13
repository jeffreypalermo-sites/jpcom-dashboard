namespace Dashboard.Tests;

/// <summary>
/// A box of the runtime diagram that leads into a view is one control: its name is no link, and the view's heading
/// row has the link the name was. A box without a view keeps its name's link.
/// </summary>
public class ViewLinksTests
{
    private const string Base = "https://raw.githubusercontent.com/org/app/architecture/ui";

    private static readonly Topology SampleTopology =
        TopologyParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "topology.sample.json"))).Topology!;

    private static readonly RuntimeManifest Sample = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;

    /// <summary>The sample manifest of uat with the architecture of ui on both of its web apps.</summary>
    private static readonly RuntimeManifest WithArchitecture = Sample with
    {
        Nodes = [.. Sample.Nodes.Select(node => node.Kind == RuntimeNodeKind.WebApp ? node with { Architecture = new Uri(Base) } : node)],
    };

    private static EnvironmentStatus Uat() => new(SampleTopology.Environments.Single(environment => environment.Name == "uat"));

    private static LinkSet Links(params (string Key, string Address)[] links) =>
        new(links.Select(link => KeyValuePair.Create(link.Key, new Uri(link.Address))));

    [Fact]
    public void TheBoxesThatZoomAreTheDatabasesWhereItHasAViewAndThoseOfADeployableWithAnArchitecture()
    {
        Assert.Empty(RuntimeZoom.Boxes(Sample, database: false));
        Assert.Equal(
            [("sqldb", "Database sqldb-cmdemo2-uat: open its tables")],
            RuntimeZoom.Boxes(Sample, database: true));
        Assert.Equal(
            ["sqldb", "app_ui_standby", "app_ui_primary"],
            RuntimeZoom.Boxes(WithArchitecture, database: true).Select(box => box.Alias));
        Assert.Equal(
            ["app_ui_standby", "app_ui_primary"],
            RuntimeZoom.Boxes(WithArchitecture, database: false).Select(box => box.Alias));
    }

    [Fact]
    public void TheNameOfABoxThatZoomsIsNoLinkAndABoxWithoutAViewKeepsItsLink()
    {
        var built = RuntimePayloadBuilder.Build(WithArchitecture, Uat(), null, TimeZoneInfo.Utc);
        Assert.All(built.Nodes, tile => Assert.NotNull(tile.NameLink));

        var zoomable = RuntimeZoom.Boxes(WithArchitecture, database: true).Select(box => box.Alias).ToHashSet(StringComparer.Ordinal);
        var payload = RuntimeZoom.WithoutNameLinks(built, zoomable);

        Assert.Equal(
            ["fd_ui", "swa_dashboard"],
            payload.Nodes.Where(tile => tile.NameLink is not null).Select(tile => tile.Alias));
        Assert.All(payload.Nodes.Where(tile => tile.NameLink is not null), tile => Assert.Equal(built.Nodes.Single(before => before.Alias == tile.Alias).NameLink, tile.NameLink));

        // The other links of a box that zooms are its own still, and so are the frames' and the regions' names.
        var primary = payload.Nodes.Single(tile => tile.Alias == "app_ui_primary");
        Assert.Equal(built.Nodes.Single(tile => tile.Alias == "app_ui_primary").Link, primary.Link);
        Assert.NotNull(primary.Link);
        Assert.Equal(built.Names, payload.Names);
        Assert.Equal(built.Regions, payload.Regions);
    }

    [Fact]
    public void WithoutAViewEveryBoxKeepsItsLink()
    {
        var built = RuntimePayloadBuilder.Build(Sample, Uat(), null, TimeZoneInfo.Utc);

        Assert.Same(built, RuntimeZoom.WithoutNameLinks(built, RuntimeZoom.Boxes(Sample, database: false).Select(box => box.Alias).ToList()));
        Assert.All(built.Nodes, tile => Assert.NotNull(tile.NameLink));
    }

    [Fact]
    public void TheDatabaseViewHasTheLinkTheDatabasesNameWas()
    {
        var uat = Uat();
        var box = DatabaseText.Box(Sample)!;
        var name = RuntimePayloadBuilder.Build(Sample, uat, null, TimeZoneInfo.Utc).Nodes.Single(tile => tile.Alias == box.Alias).NameLink!;

        var link = Assert.Single(DatabaseText.Links(box, uat.Info.Links));

        Assert.Equal(new ViewLink("Open in the Azure portal", name.Href, name.Title), link);
        Assert.Equal(uat.Info.Links![LinkSet.Database]!.AbsoluteUri, link.Href);
    }

    [Fact]
    public void WithoutALinkInTheTopologyTheDatabaseViewHasTheManifestsAndElseNone()
    {
        var cluster = new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "demo-tdd/db", Links: Links((LinkSet.Workloads, "https://portal.example.net/#aks/workloads")));

        Assert.Equal(
            [new ViewLink("Open in the Azure portal", "https://portal.example.net/#aks/workloads", LinkText.Portal("The workloads of the cluster in the Azure portal: demo-tdd/db is one of them"))],
            DatabaseText.Links(cluster, null));
        Assert.Empty(DatabaseText.Links(new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "sql-uat"), null));
        Assert.Empty(DatabaseText.Links(new RuntimeNode("sqldb", RuntimeNodeKind.Sql, "sql-uat"), LinkSet.None));
    }

    [Fact]
    public void TheApplicationViewOfOneNodeOpensTheAppAndItsPageInThePortal()
    {
        var box = new RuntimeNode(
            "ca_api", RuntimeNodeKind.ContainerApp, "ca-api", new Uri("https://ca-api.example.io"), "api",
            Links: Links((LinkSet.Portal, "https://portal.example.net/#ca-api")), Architecture: new Uri(Base));

        Assert.Equal(
            [
                new ViewLink("Open the app", "https://ca-api.example.io/", "ca-api: ca-api.example.io, as a browser opens it (opens in a new tab)"),
                new ViewLink("Open in the Azure portal", "https://portal.example.net/#ca-api", LinkText.Portal("ca-api in the Azure portal")),
            ],
            ApplicationText.Links([box], null));
    }

    [Fact]
    public void TheApplicationViewOfSeveralNodesNamesEachAndTakesThePortalFromTheTopology()
    {
        var uat = Uat();
        var boxes = ApplicationText.Boxes(WithArchitecture, "ui");
        var nodes = uat.Deployables.Single(deployable => deployable.Info.Name == "ui").Nodes;

        var links = ApplicationText.Links(boxes, uat);

        Assert.Equal(
            boxes.SelectMany(box => new[] { $"Open {box.Name}", $"{box.Name} in the Azure portal" }),
            links.Select(link => link.Text));
        Assert.Equal(
            boxes.SelectMany(box => new[] { box.Url!.AbsoluteUri, nodes.Single(node => node.Url == box.Url).Links[LinkSet.Portal]!.AbsoluteUri }),
            links.Select(link => link.Href));
        Assert.All(links, link => Assert.False(link.NoReferrer));

        // Without the topology's links only the addresses are known.
        Assert.Equal(boxes.Select(box => $"Open {box.Name}"), ApplicationText.Links(boxes, null).Select(link => link.Text));
    }

    [Fact]
    public void ANodesSiteLinkComesBeforeItsUrlAndANodeWithoutAnAddressHasNoLink()
    {
        var own = new RuntimeNode(
            "app_web_1", RuntimeNodeKind.WebApp, "ca-web", new Uri("https://probe.example.io/health"), "web",
            Links: Links((RuntimeBoxLink.Site, "https://ca-web.example.io")), Architecture: new Uri(Base));
        var plain = new RuntimeNode("app_web_2", RuntimeNodeKind.WebApp, "http-only", new Uri("http://insecure.example.io"), "web", Architecture: new Uri(Base));
        var none = new RuntimeNode("app_web_3", RuntimeNodeKind.ContainerApp, "<b>no address</b>", Deployable: "web", Architecture: new Uri(Base));

        Assert.Equal("https://ca-web.example.io/", Assert.Single(ApplicationText.Links([own], null)).Href);
        Assert.Empty(ApplicationText.Links([plain], null));
        Assert.Empty(ApplicationText.Links([none], null));
        Assert.Equal(["Open ca-web"], ApplicationText.Links([own, plain, none], null).Select(link => link.Text));
    }

    [Fact]
    public void TheDiagramAtFullSizeIsTheImagesOwnAddressAndTellsNoReferrer()
    {
        var image = ApplicationText.File(new Uri(Base), "1.0.42", "svg")!;

        Assert.Equal(
            new ViewLink("Open the diagram at full size", $"{Base}/1.0.42.svg", "The diagram of ui 1.0.42 as its repository published it (opens in a new tab)", NoReferrer: true),
            ApplicationText.DiagramLink(image, "ui", "1.0.42"));
    }
}
