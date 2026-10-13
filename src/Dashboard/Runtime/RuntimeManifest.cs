namespace Dashboard.Runtime;

/// <summary>
/// <c>runtime/index.json</c>: the environments whose runtime diagram the deployment rendered, in the order of
/// <c>system.json</c>.
/// </summary>
/// <param name="Environments">One entry per environment.</param>
/// <param name="PlantUml">The PlantUML version that rendered the diagrams.</param>
public sealed record RuntimeIndex(IReadOnlyList<RuntimeIndexEntry> Environments, string? PlantUml = null);

/// <param name="Name">The environment's name, as in <c>topology.json</c>.</param>
/// <param name="Manifest">The manifest's file, relative to <c>runtime/</c>.</param>
/// <param name="Svg">The diagram's file, relative to <c>runtime/</c>.</param>
/// <param name="Database">
/// The files of the environment's database view; null where the deployment recorded no schema (the database's box
/// then only leads to the Azure portal).
/// </param>
public sealed record RuntimeIndexEntry(string Name, string Manifest, string Svg, RuntimeDatabaseFiles? Database = null);

/// <summary>The two files of an environment's database view, relative to <c>runtime/</c>.</summary>
/// <param name="Svg">The diagram of the tables.</param>
/// <param name="Manifest">Which drawn table is which (<see cref="DatabaseManifest"/>).</param>
public sealed record RuntimeDatabaseFiles(string Svg, string Manifest);

public enum RuntimeNodeKind
{
    /// <summary>A kind this dashboard does not know: drawn, not updated.</summary>
    Other,

    /// <summary>The browser: a person, no tile.</summary>
    Person,

    /// <summary>The Front Door endpoint of a deployable: the public address.</summary>
    FrontDoor,

    /// <summary>A web app (a regional node).</summary>
    WebApp,

    /// <summary>The environment's database (Azure SQL, or SQL Server in a cluster): the browser cannot ask it.</summary>
    Sql,

    /// <summary>A Static Web App: the dashboard.</summary>
    StaticSite,

    /// <summary>
    /// Something a deployable depends on and the system does not own (an external service), drawn outside the
    /// subscription: the browser cannot ask it, and a web app's detailed health check tells its state.
    /// </summary>
    Dependency,

    /// <summary>
    /// A container app the system creates, or one its resource groups hold: the page probes its public address while
    /// it shows the diagram (<see cref="Health.ContainerAppProbe"/>), and its box also says what the pipeline's hourly
    /// health report last found there.
    /// </summary>
    ContainerApp,

    /// <summary>
    /// A box of the system's other resources (the vault, the SQL server, the identities): the browser cannot ask it,
    /// so its box says what Azure Resource Health last said of the resources it stands for (<see cref="RuntimeNode.Ids"/>).
    /// </summary>
    Resource,
}

public enum RuntimeEdgeKind
{
    Other,

    /// <summary>The browser to a public address (the Front Door endpoint, or a web app without one).</summary>
    Public,

    /// <summary>Front Door to one of its origins, by priority.</summary>
    Origin,

    /// <summary>A web app to the database.</summary>
    Sql,

    /// <summary>The browser to the dashboard.</summary>
    Dashboard,

    /// <summary>A web app to something its deployable depends on.</summary>
    Dependency,
}

/// <summary>
/// <c>runtime/&lt;env&gt;.json</c>: which drawn element of the environment's diagram is which. The dashboard finds a
/// node by its alias and a relationship by its id, and never reads names out of the SVG.
/// </summary>
/// <param name="Frames">
/// The frames that are no region (the subscription, a resource group, the Front Door profile, a plan, a cluster);
/// null in a manifest from before they were listed.
/// </param>
/// <param name="Unplaced">
/// The resources of the system that are in no environment: a list under the diagram, never a box of it
/// (<see cref="UnplacedText"/>). Null in a manifest without the list: one from before it draws them as boxes of a
/// frame "In no environment", and still does.
/// </param>
public sealed record RuntimeManifest(
    string Environment,
    IReadOnlyList<RuntimeNode> Nodes,
    IReadOnlyList<RuntimeRegion> Regions,
    IReadOnlyList<RuntimeEdge> Edges,
    IReadOnlyList<RuntimeFrame>? Frames = null,
    IReadOnlyList<RuntimeUnplaced>? Unplaced = null);

/// <summary>A resource of the system's resource groups that no environment tag places. Every string is untrusted text.</summary>
/// <param name="Name">The resource's name.</param>
/// <param name="Type">What it is, in words; null when the manifest does not say.</param>
/// <param name="Group">Its resource group.</param>
/// <param name="Words">Why it is in no environment: <c>no environment tag</c>, or <c>tagged zzz: no such environment</c>.</param>
/// <param name="Ids">Its resource ids, by which the page finds what Resource Health said of it; null without one.</param>
/// <param name="Portal">Its page in the Azure portal; null without an absolute https address.</param>
/// <param name="Site">
/// Its own public address (<c>links.site</c>, else <c>url</c>); null without an absolute https address.
/// </param>
public sealed record RuntimeUnplaced(
    string Name,
    string? Type = null,
    string? Group = null,
    string? Words = null,
    IReadOnlyList<string>? Ids = null,
    Uri? Portal = null,
    Uri? Site = null);

/// <param name="Alias">The element's name in the PlantUML source: the last part of its <c>data-qualified-name</c>.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The Azure resource's name.</param>
/// <param name="Url">
/// The address the dashboard checks (web app, Front Door endpoint) or serves from (static site); null when the
/// browser does not know one, and always for the database.
/// </param>
/// <param name="HealthCheck">
/// For a dependency: the name of the entry of its web apps' detailed health check that tells its state; null when the
/// system names none.
/// </param>
/// <param name="DependencyKind">For a dependency: what it is, in the system's own words (<c>external</c>).</param>
/// <param name="Links">
/// Where the node's name leads when the topology has no link for it (<see cref="RuntimeBoxLink"/>); null without one.
/// </param>
/// <param name="HealthPath">
/// For a container app <c>system.json</c> declares: the health path of its deployable, which the page probes; null for
/// any other node, and for an app nobody declared (the page then asks its root).
/// </param>
/// <param name="Ids">
/// The Azure resource ids the box stands for, from the resource groups' listing, by which the page finds what Resource
/// Health said of them; null in a manifest from before the ids, and where the listing has none.
/// </param>
/// <param name="Architecture">
/// For a web app or a container app: where the repository of its deployable publishes the diagram of every released
/// version (<c>&lt;address&gt;/&lt;version&gt;.json</c> and <c>.svg</c>, on another origin); the box then leads into
/// the application view. Null without one, and for anything but an absolute https address.
/// </param>
public sealed record RuntimeNode(
    string Alias,
    RuntimeNodeKind Kind,
    string Name,
    Uri? Url = null,
    string? Deployable = null,
    string? Role = null,
    string? Region = null,
    string? RegionAlias = null,
    string? HealthCheck = null,
    string? DependencyKind = null,
    Health.LinkSet? Links = null,
    string? HealthPath = null,
    IReadOnlyList<string>? Ids = null,
    Uri? Architecture = null);

/// <param name="Alias">The region boundary's alias.</param>
/// <param name="Name">The Azure region.</param>
/// <param name="Roles">primary, standby, data, static.</param>
/// <param name="Links">Where the region's name leads (<see cref="RuntimeBoxLink"/>); null without a link.</param>
public sealed record RuntimeRegion(string Alias, string Name, IReadOnlyList<string> Roles, Health.LinkSet? Links = null);

/// <summary>A frame of the diagram that is no region: it has a name and, where the deployment knows one, a link.</summary>
/// <param name="Alias">The boundary's alias.</param>
/// <param name="Kind">
/// What it is, in the deployment's word: <c>subscription</c>, <c>resourceGroup</c>, <c>frontDoorProfile</c>,
/// <c>plan</c>, <c>cluster</c>, <c>runtime</c> (the boundary of an application with its own runtime). An unknown
/// kind is linked all the same, with its name alone in the link's title.
/// </param>
/// <param name="Name">The resource's name, as drawn.</param>
/// <param name="Links">Where the frame's name leads (<see cref="RuntimeBoxLink"/>); null without a link.</param>
public sealed record RuntimeFrame(string Alias, string Kind, string Name, Health.LinkSet? Links = null);

/// <param name="Id"><c>&lt;from&gt;-to-&lt;to&gt;</c>.</param>
/// <param name="Priority">For an origin: Front Door's priority (1 first).</param>
public sealed record RuntimeEdge(string Id, string From, string To, RuntimeEdgeKind Kind, int? Priority = null);
