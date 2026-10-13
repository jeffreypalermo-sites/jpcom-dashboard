using System.Globalization;
using Dashboard.Health;

namespace Dashboard.Runtime;

/// <summary>A row of the list "In no environment": a resource, and what Resource Health last said of it.</summary>
/// <param name="Entry">The resource, as the manifest names it.</param>
/// <param name="Health">The words of its health: <c>Available · Resource Health, 40 min ago</c>, or <see cref="UnplacedText.NotRead"/>.</param>
/// <param name="HealthTone"><c>plain</c> for a recent reading, <c>muted</c> otherwise.</param>
/// <param name="HealthTitle">The tooltip of the words: where they come from and when.</param>
public sealed record UnplacedRow(RuntimeUnplaced Entry, string Health, string HealthTone, string HealthTitle);

/// <summary>
/// The resources in no environment, as the runtime view lists them under the diagram: by type, then by name, each with
/// what Resource Health last said of it in the words of the diagram's resource boxes
/// (<see cref="ResourceHealthText"/>). Words, never a colour.
/// </summary>
public static class UnplacedText
{
    /// <summary>The health of an entry without a resource id: there is nothing to look a reading up by.</summary>
    public const string NotRead = "not read";

    public const string NotReadTitle = "The manifest has no resource id for it, so the page cannot say what Azure Resource Health said of it.";

    /// <summary>The one line of the list while it is closed: <c>In no environment (3)</c>.</summary>
    public static string Summary(int count) => string.Create(CultureInfo.InvariantCulture, $"In no environment ({count})");

    /// <summary>The rows of a manifest's list, by type and then by name; empty without the list.</summary>
    public static IReadOnlyList<UnplacedRow> Rows(RuntimeManifest manifest, ResourceHealthReport? report, DateTimeOffset? now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(zone);
        if (manifest.Unplaced is not { Count: > 0 } entries)
        {
            return [];
        }

        return
        [
            .. entries
                // An entry without a type comes after the types.
                .OrderBy(entry => entry.Type is null)
                .ThenBy(entry => entry.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => Row(entry, report, now, zone)),
        ];
    }

    private static UnplacedRow Row(RuntimeUnplaced entry, ResourceHealthReport? report, DateTimeOffset? now, TimeZoneInfo zone) =>
        ResourceHealthText.Of(entry.Ids, report, now, zone) is { } words
            ? new UnplacedRow(entry, $"{words.Label} · {words.Line}", words.Tone, words.Title)
            : new UnplacedRow(entry, NotRead, "muted", NotReadTitle);
}
