using System.Globalization;
using System.Text.Json;
using Dashboard.Health;

namespace Dashboard.Runtime;

/// <summary>
/// <c>&lt;architecture&gt;/&lt;version&gt;.json</c>: the assemblies of one released version of a deployable, as its
/// repository published them next to the diagram <c>&lt;version&gt;.svg</c>. It is not the dashboard's own file, so
/// every part of it is optional and every word of it is untrusted text.
/// </summary>
/// <param name="Kind">What the artifact is, in the repository's words (<c>container image</c>); null when the file does not say.</param>
/// <param name="Process">How the artifact starts (<c>dotnet Ui.Server.dll</c>); null when not known.</param>
/// <param name="Entry">The entry assembly; null for an artifact that is not .NET.</param>
/// <param name="Truncated">The application has more assemblies than the file lists.</param>
public sealed record ApplicationArchitecture(
    string? Kind,
    string? Process,
    string? Entry,
    IReadOnlyList<ApplicationAssembly> Assemblies,
    bool Truncated);

/// <param name="Entry">The assembly the process starts with.</param>
/// <param name="References">The assemblies of the application it references.</param>
public sealed record ApplicationAssembly(string Name, bool Entry, IReadOnlyList<string> References, IReadOnlyList<ApplicationPackage> Packages);

/// <param name="Version">The package's version; null when the file does not say.</param>
public sealed record ApplicationPackage(string Name, string? Version);

/// <summary>The runtime view asks for the application view of a deployable, or for its diagram again.</summary>
/// <param name="Deployable">The deployable whose view is to be shown; null for the diagram.</param>
/// <param name="Step">The change is a step of the browser's history: its Back undoes it.</param>
public sealed record RuntimeApplicationChange(string Environment, string? Deployable, bool Step);

/// <summary>The version the application view shows of a deployable.</summary>
/// <param name="Text">The version, without build metadata.</param>
/// <param name="Runs">A node reported it; false for the version the environment pins.</param>
public sealed record ApplicationVersion(string Text, bool Runs);

public enum ApplicationReadState
{
    /// <summary>The file answered and is a JSON object.</summary>
    Read,

    /// <summary>HTTP 404: the repository published no diagram of that version.</summary>
    Missing,

    /// <summary>Anything else: no answer, refused, another status, no JSON object.</summary>
    Failed,
}

/// <summary>The outcome of asking for <c>&lt;version&gt;.json</c>.</summary>
public sealed record ApplicationRead(ApplicationReadState State, ApplicationArchitecture? Architecture = null)
{
    public static ApplicationRead Missing { get; } = new(ApplicationReadState.Missing);

    public static ApplicationRead Failed { get; } = new(ApplicationReadState.Failed);
}

/// <summary>
/// Reads <c>&lt;version&gt;.json</c>. Somebody else wrote it: a part that is absent or of another type is left out,
/// and a file that is no JSON object is no file. Unknown fields are ignored.
/// </summary>
public static class ApplicationParser
{
    /// <summary>The most assemblies, references and packages of one assembly that are kept, and the longest text.</summary>
    private const int MostAssemblies = 2000;
    private const int MostEntries = 1000;
    private const int LongestText = 300;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ApplicationArchitecture? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var entry = Text(root, "entry");
            var assemblies = new List<ApplicationAssembly>();
            foreach (var element in JsonRead.Items(root, "assemblies").Take(MostAssemblies))
            {
                if (Text(element, "name") is not { } name)
                {
                    continue;
                }

                var packages = JsonRead.Items(element, "packages")
                    .Take(MostEntries)
                    .Where(package => Text(package, "name") is not null)
                    .Select(package => new ApplicationPackage(Text(package, "name")!, Text(package, "version")))
                    .ToList();
                assemblies.Add(new ApplicationAssembly(
                    name,
                    JsonRead.Flag(element, "entry") ?? string.Equals(name, entry, StringComparison.Ordinal),
                    Strings(element, "references"),
                    packages));
            }

            return new ApplicationArchitecture(Text(root, "kind"), Text(root, "process"), entry, assemblies, JsonRead.Flag(root, "truncated") ?? false);
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        JsonRead.Text(parent, name) is { } text ? Shorten(text) : null;

    private static string Shorten(string text) => text.Length <= LongestText ? text : text[..LongestText];

    private static List<string> Strings(JsonElement parent, string name) =>
    [
        .. JsonRead.Items(parent, name)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => Shorten(text!))
            .Take(MostEntries),
    ];
}

/// <summary>The words of the application view, and what it decides: which boxes lead into it, which version, which files.</summary>
public static class ApplicationText
{
    private const int LongestName = 64;
    private const int LongestVersion = 40;

    /// <summary>A deployable's name as the address takes it: letters, digits, <c>-</c> and <c>_</c>.</summary>
    public static bool IsName(string? deployable) =>
        deployable is { Length: > 0 and <= LongestName }
        && deployable.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// A version that may name a file: one to forty of letters, digits and <c>. _ + -</c>. The page asks for no file
    /// of any other version.
    /// </summary>
    public static bool IsVersion(string? version) =>
        version is { Length: > 0 and <= LongestVersion }
        && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-');

    /// <summary>
    /// The nodes of the runtime diagram that open an application view: a web app or a container app whose manifest
    /// entry names where its deployable's diagrams are published.
    /// </summary>
    public static IReadOnlyList<RuntimeNode> Boxes(RuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return [.. manifest.Nodes.Where(node => node.Architecture is not null && IsName(node.Deployable))];
    }

    /// <summary>The boxes of one deployable, the primary first; none for a deployable without a diagram, and for a name the address refuses.</summary>
    public static IReadOnlyList<RuntimeNode> Boxes(RuntimeManifest manifest, string? deployable) =>
        IsName(deployable)
            ? [.. Boxes(manifest).Where(node => string.Equals(node.Deployable, deployable, StringComparison.OrdinalIgnoreCase)).OrderBy(node => IsPrimary(node.Role) ? 0 : 1)]
            : [];

    /// <summary>The accessible name of an application's box in the runtime diagram.</summary>
    public static string BoxLabel(RuntimeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return $"Application {node.Deployable}: open its assemblies";
    }

    /// <summary>
    /// The version the view shows: the one a node runs (the primary's when the nodes run several, else the first one
    /// known), else the one the environment pins; null when neither is known.
    /// </summary>
    /// <param name="nodes">The deployable's nodes in their order: the role and the version each last reported.</param>
    public static ApplicationVersion? Version(IEnumerable<(string? Role, string? Version)> nodes, string? pinned)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var running = nodes.Where(node => !string.IsNullOrWhiteSpace(node.Version)).ToList();
        if (running.Count > 0)
        {
            var chosen = running.FirstOrDefault(node => IsPrimary(node.Role), running[0]);
            return new ApplicationVersion(chosen.Version!.Trim(), Runs: true);
        }

        return string.IsNullOrWhiteSpace(pinned) ? null : new ApplicationVersion(pinned.Trim(), Runs: false);
    }

    /// <summary>
    /// The version of a deployable in an environment the page checks: what the nodes the diagram draws report (a node
    /// is found by its address, as its tile is), and the pin of the deployable's own file or of <c>versions.json</c>.
    /// </summary>
    public static ApplicationVersion? Version(IReadOnlyList<RuntimeNode> boxes, EnvironmentStatus? environment)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (environment is null || boxes.Count == 0)
        {
            return null;
        }

        var name = boxes[0].Deployable!;
        var checkedNodes = environment.Deployables.SelectMany(deployable => deployable.Nodes).ToList();
        var nodes = boxes.Select(box => (box.Role, box.Url is null ? null : checkedNodes.FirstOrDefault(node => node.Url == box.Url)?.Version));
        var status = environment.Deployables.FirstOrDefault(deployable => string.Equals(deployable.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        var pinned = status?.Pinned is { } own ? own.Of(status.Info.Name) : environment.Pinned.Of(status?.Info.Name ?? name);
        return Version(nodes, pinned);
    }

    /// <summary>The line under the heading: <c>Version 1.0.42, as it runs</c>.</summary>
    public static string VersionLine(string deployable, ApplicationVersion? version) => version switch
    {
        null => $"The version of {deployable} is not known yet.",
        { Runs: true } => $"Version {version.Text}, as it runs",
        _ => $"Version {version.Text}, as pinned",
    };

    /// <summary>The line's tooltip: where the version is from.</summary>
    public static string VersionTitle(string deployable, string environment, ApplicationVersion? version) => version switch
    {
        null => $"No node of {deployable} in {environment} reported a version, and the environment pins none: no diagram is asked for.",
        { Runs: true } => $"The version a node of {deployable} in {environment} reported; the primary's when the nodes run several.",
        _ => $"No node of {deployable} in {environment} reported a version: this is the one the environment pins in Git.",
    };

    /// <summary>
    /// A file of one version, <c>&lt;base&gt;/&lt;version&gt;.json</c> or <c>.svg</c>; null for a version that may
    /// name no file.
    /// </summary>
    public static Uri? File(Uri architecture, string? version, string extension)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        return IsVersion(version)
            ? new Uri($"{architecture.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(version!)}.{extension}")
            : null;
    }

    /// <summary>The diagram's text for a reader who does not see it: <c>Assemblies of ui 1.0.42: 3 assemblies, entry Ui.Server</c>.</summary>
    public static string AltText(string deployable, string version, ApplicationArchitecture architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        var count = architecture.Assemblies.Count;
        if (count == 0)
        {
            return architecture.Process is { } process
                ? $"Architecture of {deployable} {version}: no assemblies, process {process}"
                : $"Architecture of {deployable} {version}: no assemblies";
        }

        var counted = count == 1 ? "1 assembly" : string.Create(CultureInfo.InvariantCulture, $"{count} assemblies");
        return architecture.Entry is { } entry
            ? $"Assemblies of {deployable} {version}: {counted}, entry {entry}"
            : $"Assemblies of {deployable} {version}: {counted}";
    }

    /// <summary>What the artifact is and how it starts: <c>container image, process: dotnet Ui.Server.dll</c>; null when the file says neither.</summary>
    public static string? KindLine(ApplicationArchitecture architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        return (architecture.Kind, architecture.Process) switch
        {
            ({ } kind, { } process) => $"{kind}, process: {process}",
            ({ } kind, null) => kind,
            (null, { } process) => $"process: {process}",
            _ => null,
        };
    }

    /// <summary>The line of a file that lists only a part of the assemblies; null for one that lists all.</summary>
    public static string? TruncatedLine(ApplicationArchitecture architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        return architecture.Truncated ? "The diagram and the list show only a part of the assemblies." : null;
    }

    /// <summary>The line of a version whose file did not answer; null for one that was read.</summary>
    public static string? ProblemLine(string deployable, string version, ApplicationReadState state) => state switch
    {
        ApplicationReadState.Missing => $"No architecture diagram of {deployable} {version} yet: its repository publishes one with each release.",
        ApplicationReadState.Failed => $"The architecture diagram of {deployable} {version} could not be read.",
        _ => null,
    };

    /// <summary>The line of a version the page asks no file for.</summary>
    public static string RefusedLine(string deployable) => $"The version of {deployable} names no file: no diagram is asked for.";

    /// <summary>
    /// The links of the view's heading row: where the names of the deployable's boxes led before the boxes zoomed.
    /// Per box its own address (<c>links.site</c>, else <c>url</c>) and, where it has one, its page in the Azure
    /// portal (<c>links.portal</c> of its node in the topology, else of the manifest). One box: "Open the app" and
    /// "Open in the Azure portal"; several: each link names its box.
    /// </summary>
    /// <param name="boxes">The deployable's boxes, as <see cref="Boxes(RuntimeManifest, string?)"/> orders them.</param>
    /// <param name="environment">The monitor's environment of the diagram; null when the topology has none.</param>
    public static IReadOnlyList<ViewLink> Links(IReadOnlyList<RuntimeNode> boxes, EnvironmentStatus? environment)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var checkedNodes = environment?.Deployables.SelectMany(deployable => deployable.Nodes).ToList() ?? [];
        var links = new List<ViewLink>();
        foreach (var box in boxes)
        {
            if ((box.Links?[RuntimeBoxLink.Site] ?? box.Url) is { } site && site.Scheme == Uri.UriSchemeHttps)
            {
                links.Add(new ViewLink(
                    boxes.Count == 1 ? "Open the app" : $"Open {box.Name}",
                    site.AbsoluteUri,
                    $"{box.Name}: {site.Host}, as a browser opens it{RuntimeBoxLink.NewTabSuffix}"));
            }

            var topology = box.Url is null ? null : checkedNodes.FirstOrDefault(node => node.Url == box.Url)?.Links[LinkSet.Portal];
            if ((topology ?? box.Links?[LinkSet.Portal]) is { } portal)
            {
                links.Add(new ViewLink(
                    boxes.Count == 1 ? "Open in the Azure portal" : $"{box.Name} in the Azure portal",
                    portal.AbsoluteUri,
                    LinkText.Portal($"{box.Name} in the Azure portal")));
            }
        }

        return links;
    }

    /// <summary>"Open the diagram at full size": the image's own address, for a diagram the view fitted to its frame.</summary>
    public static ViewLink DiagramLink(Uri image, string deployable, string version)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ViewLink(
            "Open the diagram at full size",
            image.AbsoluteUri,
            $"The diagram of {deployable} {version} as its repository published it{RuntimeBoxLink.NewTabSuffix}",
            NoReferrer: true);
    }

    /// <summary>A package as the list names it: <c>Serilog 4.0.1</c>.</summary>
    public static string Package(ApplicationPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Version is { } version ? $"{package.Name} {version}" : package.Name;
    }

    private static bool IsPrimary(string? role) => string.Equals(role, NodeInfo.PrimaryRole, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// When the file of a version is asked for: once per version while the view is open. The files of a version never
/// change, so a check round of the page asks nothing; a view opened again starts anew.
/// </summary>
public static class ApplicationRefresh
{
    /// <param name="asked">The version last asked for; null when none was.</param>
    /// <param name="version">The version the view shows now; null when none is known.</param>
    public static bool IsDue(string? asked, string? version) =>
        ApplicationText.IsVersion(version) && !string.Equals(asked, version, StringComparison.Ordinal);
}
