using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// What Azure Resource Health last said of the system's resources: the <c>health</c> of each record of
/// <c>containers.json</c>, which workflow <c>delivery</c> publishes hourly on branch <c>status</c> next to
/// <c>delivery.json</c> (<c>scripts/write-containers.ps1</c>, as the system's Reader identity). Found by resource id,
/// whatever its case.
/// </summary>
/// <param name="Generated">When the file's content last changed.</param>
public sealed record ResourceHealthReport(DateTimeOffset? Generated, IReadOnlyDictionary<string, ResourceHealthReading> Readings)
{
    public ResourceHealthReading? Find(string id) => Readings.GetValueOrDefault(id);

    /// <summary>The readings of the file; null when it is not a JSON object with <c>resources</c>.</summary>
    public static ResourceHealthReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (JsonRead.Section(root, "resources") is not { } groups)
            {
                return null;
            }

            var readings = new Dictionary<string, ResourceHealthReading>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups.EnumerateObject().Where(group => group.Value.ValueKind == JsonValueKind.Array))
            {
                foreach (var record in group.Value.EnumerateArray())
                {
                    if (JsonRead.Text(record, "id") is { } id && ResourceHealthReading.Read(JsonRead.Section(record, "health")) is { } reading)
                    {
                        readings[id] = reading;
                    }
                }
            }

            return new ResourceHealthReport(JsonRead.Time(root, "generated"), readings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What the pipeline read of one resource in one run.</summary>
/// <param name="Status">
/// From Resource Health: <c>available</c>, <c>degraded</c> or <c>unavailable</c>. Where it has no answer (a type it does
/// not cover, or "Unknown"): the provisioning state, as <c>succeeded</c>, <c>failed</c> and the like; <c>unknown</c>
/// without either.
/// </param>
/// <param name="Source"><see cref="FromResourceHealth"/>, <see cref="FromProvisioningState"/> or <see cref="None"/>.</param>
/// <param name="CheckedAt">When the pipeline read it.</param>
/// <param name="Summary">What Resource Health said in words; null without it.</param>
public sealed record ResourceHealthReading(string Status, string Source, DateTimeOffset CheckedAt, string? Summary = null)
{
    public const string FromResourceHealth = "resourceHealth";
    public const string FromProvisioningState = "provisioningState";
    public const string None = "none";

    private const int LongestSummary = 200;

    /// <summary>A reading; null without a status of a word or two, or without its time.</summary>
    internal static ResourceHealthReading? Read(JsonElement? section)
    {
        var status = JsonRead.Text(section, "status")?.ToLowerInvariant();
        if (status is not { Length: <= 40 } || !status.All(character => char.IsAsciiLetterLower(character) || character == ' ')
            || JsonRead.Time(section, "checkedAt") is not { } checkedAt)
        {
            return null;
        }

        var source = JsonRead.Text(section, "source") switch
        {
            FromResourceHealth => FromResourceHealth,
            FromProvisioningState => FromProvisioningState,
            _ => None,
        };
        var summary = JsonRead.Text(section, "summary");
        return new ResourceHealthReading(status, source, checkedAt, summary is { Length: > LongestSummary } ? summary[..LongestSummary] : summary);
    }
}

/// <summary>The words of a box from what Resource Health said of the resources it stands for.</summary>
/// <param name="Label">The badge: the most urgent state of its resources.</param>
/// <param name="Line">Where the words come from and how long ago, or the count of each state for a box of several.</param>
/// <param name="Tone"><c>plain</c> while the oldest reading is at most two hours old, <c>muted</c> otherwise.</param>
/// <param name="Title">The tooltip: each resource, its state, where it comes from and when.</param>
public sealed record ResourceHealthWords(string Label, string Line, string Tone, string Title);

/// <summary>
/// Resource Health on a box, in neutral words, never a colour: <c>Available</c> and <c>Resource Health, 40 min ago</c>.
/// The pipeline reads it hourly; this page does not ask Azure.
/// </summary>
public static class ResourceHealthText
{
    /// <summary>The tooltip's first words.</summary>
    public const string Source = "Azure Resource Health, read hourly by the pipeline (workflow delivery, as the system's Reader identity); this page does not ask Azure.";

    private const string NotKnown = "Not known";
    private const int MostNamed = 8;

    /// <summary>The words of a box of these resources; null for a box without ids (a manifest from before them).</summary>
    public static ResourceHealthWords? Of(IReadOnlyList<string>? ids, ResourceHealthReport? report, DateTimeOffset? now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (ids is not { Count: > 0 })
        {
            return null;
        }

        if (report is null)
        {
            return new ResourceHealthWords(
                NotKnown,
                "no Resource Health read yet",
                "muted",
                $"{Source} The page has not read it: containers.json on branch status, next to delivery.json, is not there or not read yet.");
        }

        var readings = ids.Select(id => (Id: id, Reading: report.Find(id))).ToList();
        var labels = readings.Select(entry => Label(entry.Reading)).ToList();
        var label = labels.OrderBy(Severity).First();
        var read = readings.Select(entry => entry.Reading).OfType<ResourceHealthReading>().ToList();
        var oldest = read.Count == 0 ? (DateTimeOffset?)null : read.Min(reading => reading.CheckedAt);
        var age = oldest is { } at && now is { } moment ? $", {TimeText.Ago(at, moment)}" : string.Empty;
        var words = ids.Count == 1
            ? (readings[0].Reading?.Source switch
            {
                ResourceHealthReading.FromResourceHealth => "Resource Health",
                ResourceHealthReading.FromProvisioningState => "provisioning state",
                ResourceHealthReading.None => "no Resource Health",
                _ => "not in the hourly reading",
            }) + age
            : string.Join(" · ", labels.GroupBy(word => word).OrderBy(group => Severity(group.Key)).Select(group => string.Create(CultureInfo.InvariantCulture, $"{group.Count()} {group.Key.ToLowerInvariant()}"))) + age;
        var recent = oldest is { } since && now is { } current && current - since <= HourlyCheckText.Recent;

        var title = new List<string> { Source };
        title.AddRange(readings.Take(MostNamed).Select(entry => Describe(entry.Id, entry.Reading, now, zone)));
        if (readings.Count > MostNamed)
        {
            title.Add(string.Create(CultureInfo.InvariantCulture, $"And {readings.Count - MostNamed} more."));
        }

        return new ResourceHealthWords(label, words, recent ? "plain" : "muted", string.Join('\n', title));
    }

    /// <summary>The badge's word for one reading: <c>Available</c>, <c>Provisioned</c>, <c>Failed</c>, <c>Not known</c>.</summary>
    public static string Label(ResourceHealthReading? reading) => reading switch
    {
        { Source: ResourceHealthReading.FromResourceHealth, Status: "available" or "degraded" or "unavailable" } => Capitalized(reading.Status),
        { Source: ResourceHealthReading.FromProvisioningState, Status: "succeeded" } => "Provisioned",
        { Source: ResourceHealthReading.FromProvisioningState } => Capitalized(reading.Status),
        _ => NotKnown,
    };

    /// <summary>The order of the words, most urgent first: a box of several shows the first.</summary>
    private static int Severity(string label) => label switch
    {
        "Unavailable" => 0,
        "Degraded" => 2,
        NotKnown => 3,
        "Provisioned" => 4,
        "Available" => 5,
        _ => 1,
    };

    private static string Describe(string id, ResourceHealthReading? reading, DateTimeOffset? now, TimeZoneInfo zone)
    {
        var name = id.TrimEnd('/').Split('/')[^1];
        if (reading is null)
        {
            return $"{name}: not in the hourly reading.";
        }

        var source = reading.Source switch
        {
            ResourceHealthReading.FromResourceHealth => "by Resource Health",
            ResourceHealthReading.FromProvisioningState => "Resource Health has no answer for it; its provisioning state",
            _ => "neither Resource Health nor a provisioning state answered",
        };
        var when = now is { } moment ? $"{TimeText.DateAndTime(reading.CheckedAt, zone)} ({TimeText.Ago(reading.CheckedAt, moment)})" : TimeText.DateAndTime(reading.CheckedAt, zone);
        var summary = reading.Summary is { } said ? $" {said}" : string.Empty;
        return $"{name}: {reading.Status} ({source}), read {when}.{summary}";
    }

    private static string Capitalized(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
