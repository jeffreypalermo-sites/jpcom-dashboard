using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>The state of one entry of a detailed health check, as ASP.NET Core's health checks name them.</summary>
public enum CheckState
{
    Healthy,

    /// <summary>It works, with a reservation: the app answers its health check with HTTP 200 all the same.</summary>
    Degraded,

    Unhealthy,

    /// <summary>A status this dashboard does not know.</summary>
    Unknown,
}

/// <summary>One entry of a detailed health check: one thing the app depends on and checks.</summary>
/// <param name="Name">The entry's name, as the app registered the check (<c>DataAccess</c>).</param>
/// <param name="State">What the check found.</param>
/// <param name="Description">The check's own words; null when it gave none.</param>
/// <param name="DurationMs">How long the check took, in milliseconds.</param>
public sealed record HealthCheckEntry(string Name, CheckState State, string? Description = null, double? DurationMs = null)
{
    public bool IsHealthy => State == CheckState.Healthy;
}

/// <summary>
/// What a node's health check found, entry by entry: the answer of its detailed health check
/// (<c>deployables[].healthDetailPath</c> of the topology, for example <c>/_healthcheck/detailed</c>). The health
/// check itself answers with a status only; this says which dependency is the reason.
/// </summary>
/// <param name="Entries">The entries, in the order of the answer.</param>
/// <param name="ReadAt">When the dashboard read it.</param>
public sealed record HealthDetail(IReadOnlyList<HealthCheckEntry> Entries, DateTimeOffset ReadAt)
{
    /// <summary>The entries that are not healthy, the worst first (unhealthy, degraded, unknown), then in the answer's order.</summary>
    public IReadOnlyList<HealthCheckEntry> NotHealthy =>
        [.. Entries.Where(entry => !entry.IsHealthy).OrderBy(entry => HealthDetailText.Severity(entry.State))];

    /// <summary>The entry of that name, whatever its case; null when the answer has none.</summary>
    public HealthCheckEntry? Find(string? name) =>
        name is null ? null : Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The endpoint's answer; null when it is not a JSON object with <c>entries</c> that name at least one check (an
    /// older app, an error page). An entry without a name is left out; a status that is absent or not known is
    /// <see cref="CheckState.Unknown"/>.
    /// </summary>
    public static HealthDetail? Parse(string? json, DateTimeOffset readAt)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var entries = JsonRead.Items(document.RootElement, "entries")
                .Select(entry => (Name: JsonRead.Text(entry, "name"), Element: entry))
                .Where(entry => entry.Name is not null)
                .Select(entry => new HealthCheckEntry(
                    entry.Name!,
                    StateOf(JsonRead.Text(entry.Element, "status")),
                    JsonRead.Text(entry.Element, "description"),
                    JsonRead.Number(entry.Element, "durationMs")))
                .ToList();
            return entries.Count == 0 ? null : new HealthDetail(entries, readAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CheckState StateOf(string? status) => status?.ToLowerInvariant() switch
    {
        "healthy" => CheckState.Healthy,
        "degraded" => CheckState.Degraded,
        "unhealthy" => CheckState.Unhealthy,
        _ => CheckState.Unknown,
    };
}

/// <summary>The entries of a detailed health check in the words of the tiles.</summary>
public static class HealthDetailText
{
    /// <summary>The state's word: a mark is never colour alone.</summary>
    public static string Label(CheckState state) => state switch
    {
        CheckState.Healthy => "Healthy",
        CheckState.Degraded => "Degraded",
        CheckState.Unhealthy => "Unhealthy",
        _ => "Not known",
    };

    /// <summary>The order of the states, the worst first: unhealthy, degraded, not known, healthy.</summary>
    public static int Severity(CheckState state) => state switch
    {
        CheckState.Unhealthy => 0,
        CheckState.Degraded => 1,
        CheckState.Unknown => 2,
        _ => 3,
    };

    /// <summary>The icon of a mark, by shape: check, warning triangle, cross, dots.</summary>
    public static HealthState Icon(CheckState state) => state switch
    {
        CheckState.Healthy => HealthState.Healthy,
        CheckState.Degraded => HealthState.Unhealthy,
        CheckState.Unhealthy => HealthState.Unreachable,
        _ => HealthState.Pending,
    };

    /// <summary>A mark's tooltip: <c>DataAccess: Healthy. Database connection successful. Took 42.1 ms.</c></summary>
    public static string Title(HealthCheckEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var parts = new List<string> { $"{entry.Name}: {Label(entry.State)}." };
        if (entry.Description is { } description)
        {
            parts.Add(description.EndsWith('.') ? description : $"{description}.");
        }

        if (entry.DurationMs is { } duration)
        {
            parts.Add($"Took {Duration(duration)}.");
        }

        return string.Join(' ', parts);
    }

    /// <summary><c>42.1 ms</c>; under a tenth of a millisecond <c>under 0.1 ms</c>; from a second on <c>1.2 s</c>.</summary>
    public static string Duration(double milliseconds) =>
        milliseconds < 0.05 ? "under 0.1 ms"
        : milliseconds < 1000 ? string.Create(CultureInfo.InvariantCulture, $"{milliseconds:0.#} ms")
        : string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000:0.#} s");

    /// <summary>
    /// One short line for all entries: <c>4 checks healthy</c>; the one that is not, by name, <c>DataAccess
    /// unhealthy</c>; or how many are not, <c>2 of 4 checks not healthy</c> (the marks' tooltips name them).
    /// </summary>
    public static string Summary(HealthDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var failing = detail.NotHealthy;
        var count = detail.Entries.Count;
        return failing.Count switch
        {
            0 => count == 1 ? "1 check healthy" : string.Create(CultureInfo.InvariantCulture, $"{count} checks healthy"),
            1 => $"{Brief(failing[0].Name, 20)} {Label(failing[0].State).ToLowerInvariant()}",
            _ => string.Create(CultureInfo.InvariantCulture, $"{failing.Count} of {count} checks not healthy"),
        };
    }

    /// <summary>
    /// Words cut to what a line of the diagram holds: the first sentence when that fits, otherwise the start with an
    /// ellipsis. The tooltip has them in full.
    /// </summary>
    public static string Brief(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sentence = text.IndexOf(". ", StringComparison.Ordinal) is var end and >= 0 ? text[..end] : text.TrimEnd('.');
        return sentence.Length <= length ? sentence : $"{text[..(length - 1)].TrimEnd()}…";
    }

    /// <summary>
    /// Which entries are the reason a node's health check fails, each with its state and its own words:
    /// <c>Not healthy: DataAccess (Unhealthy: Login failed), LlmGateway (Degraded)</c>. Null when every entry is healthy.
    /// </summary>
    public static string? Failed(HealthDetail? detail)
    {
        if (detail is null || detail.NotHealthy is not { Count: > 0 } failing)
        {
            return null;
        }

        return $"Not healthy: {string.Join(", ", failing.Select(entry => entry.Description is { } description ? $"{entry.Name} ({Label(entry.State)}: {description.TrimEnd('.')})" : $"{entry.Name} ({Label(entry.State)})"))}";
    }
}
