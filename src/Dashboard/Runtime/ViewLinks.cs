using Dashboard.Health;

namespace Dashboard.Runtime;

/// <summary>
/// A link in the heading row of a view a box of the runtime diagram led into: it leaves the page, in a new tab.
/// </summary>
/// <param name="Text">The link's words.</param>
/// <param name="Href">An absolute https address.</param>
/// <param name="Title">Where it goes, and that it opens a new tab.</param>
/// <param name="NoReferrer">The destination is not told which page the reader came from (a file on another origin).</param>
public sealed record ViewLink(string Text, string Href, string Title, bool NoReferrer = false);

/// <summary>
/// The boxes of the runtime diagram that lead into a view of the page. Such a box is one control: its whole area
/// zooms, its name too, so its name is no link, and the view has the link the name was
/// (<see cref="DatabaseText.Links"/>, <see cref="ApplicationText.Links"/>).
/// </summary>
public static class RuntimeZoom
{
    /// <summary>
    /// The boxes that zoom, each with its accessible name: the database's first, where the deployment wrote the
    /// files of the database view, then every box of a deployable whose architecture the manifest names.
    /// </summary>
    /// <param name="manifest">The manifest of the environment's diagram.</param>
    /// <param name="database">The environment has the files of the database view.</param>
    public static IReadOnlyList<(string Alias, string Label)> Boxes(RuntimeManifest manifest, bool database)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var boxes = ApplicationText.Boxes(manifest).Select(box => (box.Alias, Label: ApplicationText.BoxLabel(box))).ToList();
        if (database && DatabaseText.Box(manifest) is { } sql)
        {
            boxes.Insert(0, (sql.Alias, DatabaseText.BoxLabel(sql)));
        }

        return boxes;
    }

    /// <summary>The payload with no link on the name of the boxes that zoom; every other box keeps its own.</summary>
    public static RuntimePayload WithoutNameLinks(RuntimePayload payload, IReadOnlyCollection<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(aliases);
        return aliases.Count == 0
            ? payload
            : payload with { Nodes = [.. payload.Nodes.Select(tile => tile.NameLink is not null && aliases.Contains(tile.Alias) ? tile with { NameLink = null } : tile)] };
    }
}
