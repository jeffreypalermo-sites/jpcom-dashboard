using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>
/// What the system cost in Azure: the content of the file at <c>system.costUrl</c> of the topology (<c>cost.json</c>,
/// which a workflow of the system repository publishes from Azure Cost Management). Per environment, for what no
/// environment owns (<see cref="SharedName"/>) and for the whole system: the last complete UTC day, the seven days
/// that end with it and its month so far. Every part is optional, and nothing of it is live: the numbers are those of
/// <see cref="AsOf"/>.
/// </summary>
/// <param name="Generated">When the file's content last changed. It is not the time of a check.</param>
/// <param name="Currency">The currency Azure bills in, such as <c>USD</c>.</param>
/// <param name="AsOf">The last complete UTC day the numbers include.</param>
/// <param name="System">The whole system; null when the file does not say.</param>
/// <param name="Entries">The environments, in the file's order, with the entry of what they share.</param>
public sealed record CostReport(DateTimeOffset? Generated, string? Currency, DateOnly? AsOf, CostAmounts? System, IReadOnlyList<CostEntry> Entries)
{
    /// <summary>The name of the entry of what carries no environment: the resources every environment shares.</summary>
    public const string SharedName = "shared";

    /// <summary>What no single environment owns; null when the file has no such entry.</summary>
    public CostEntry? Shared => Find(SharedName);

    public static bool IsShared(string name) => string.Equals(name, SharedName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The entry of an environment; null when the file has none.</summary>
    public CostEntry? Find(string name) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entries that are neither an environment the page shows nor <see cref="SharedName"/>: what Azure still
    /// bills under the name of an environment the topology no longer has.
    /// </summary>
    public IReadOnlyList<CostEntry> Others(IEnumerable<string> environments)
    {
        ArgumentNullException.ThrowIfNull(environments);
        var shown = environments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Entries.Where(entry => !IsShared(entry.Name) && !shown.Contains(entry.Name))];
    }

    /// <summary>The file's content; null when it is not a JSON object with <c>system</c> or <c>environments</c>.</summary>
    public static CostReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var entries = JsonRead.Items(root, "environments").Select(CostEntry.Read).OfType<CostEntry>().ToList();
            var system = JsonRead.Section(root, "system") is { } total ? CostAmounts.Read(total) : null;
            var asOf = DateOnly.TryParseExact(JsonRead.Text(root, "asOf"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateOnly?)null;
            return entries.Count == 0 && system is null
                ? null
                : new CostReport(JsonRead.Time(root, "generated"), JsonRead.Text(root, "currency"), asOf, system, entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Three sums of complete UTC days; one that Azure did not give is null, and the page shows a dash.</summary>
/// <param name="Yesterday">The last complete UTC day (<see cref="CostReport.AsOf"/>).</param>
/// <param name="Last7Days">The seven days that end with it.</param>
/// <param name="MonthToDate">The first of its month up to it.</param>
public sealed record CostAmounts(double? Yesterday, double? Last7Days, double? MonthToDate)
{
    internal static CostAmounts Read(JsonElement element) =>
        new(Amount(element, "yesterday"), Amount(element, "last7Days"), Amount(element, "monthToDate"));

    /// <summary>An amount of money: any finite number, also below zero (a refund); null for anything else.</summary>
    internal static double? Amount(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDouble(out var amount) && double.IsFinite(amount)
            ? amount
            : null;
}

/// <summary>The cost of one environment, or of what the environments share.</summary>
/// <param name="TopServices">The services that cost most this month, the most expensive first.</param>
/// <param name="Estimate">
/// The environment's estimated part of what the environments share (a cluster that runs them all); null when the file
/// gives none. It is not added to <paramref name="Amounts"/>: the shared entry still holds the whole.
/// </param>
public sealed record CostEntry(string Name, CostAmounts Amounts, IReadOnlyList<CostService> TopServices, CostEstimate? Estimate = null)
{
    internal static CostEntry? Read(JsonElement element) =>
        JsonRead.Text(element, "name") is { } name
            ? new CostEntry(
                name,
                CostAmounts.Read(element),
                [.. JsonRead.Items(element, "topServices")
                    .Select(service => (Name: JsonRead.Text(service, "name"), Amount: CostAmounts.Amount(service, "monthToDate")))
                    .Where(service => service.Name is not null)
                    .Select(service => new CostService(service.Name!, service.Amount))],
                JsonRead.Section(element, "estimate") is { } estimate ? CostEstimate.Read(estimate) : null)
            : null;
}

/// <summary>
/// An environment's part of a cost the environments share, estimated by the file's publisher: the shared cost times
/// the share of CPU and memory the environment's pods request of what all pods request.
/// </summary>
/// <param name="Share">The share, 0 to 1; null when the file does not say.</param>
public sealed record CostEstimate(double? Share, CostAmounts Amounts)
{
    internal static CostEstimate? Read(JsonElement element)
    {
        var amounts = CostAmounts.Read(element);
        var share = CostAmounts.Amount(element, "share") is { } value and >= 0 and <= 1 ? value : (double?)null;
        return amounts is { Yesterday: null, Last7Days: null, MonthToDate: null } ? null : new CostEstimate(share, amounts);
    }
}

/// <summary>An Azure service (as Cost Management names it) and what it cost in the month so far.</summary>
public sealed record CostService(string Name, double? MonthToDate);

/// <summary>An amount with the days it covers: <c>$1.52</c> and <c>yesterday</c>.</summary>
public sealed record CostPart(string Amount, string Period);

/// <summary>The cost in the words of the page. Nothing here says "now": every wording names the days it covers.</summary>
public static class CostText
{
    /// <summary>What stands for a number Azure did not give.</summary>
    public const string NoNumber = "—";

    /// <summary>
    /// <c>$1.52</c>, <c>$1,204.00</c>, <c>-$0.30</c>; another currency by its code (<c>1.52 EUR</c>); a dash for no
    /// number.
    /// </summary>
    public static string Money(double? amount, string? currency)
    {
        if (amount is not { } value)
        {
            return NoNumber;
        }

        // An amount that rounds to nothing has no sign.
        var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
        var sign = value < 0 && rounded > 0 ? "-" : string.Empty;
        var number = rounded.ToString("#,0.00", CultureInfo.InvariantCulture);
        return currency switch
        {
            null => $"{sign}{number}",
            _ when string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) => $"{sign}${number}",
            _ => $"{sign}{number} {currency}",
        };
    }

    /// <summary>
    /// The day the first number covers: <c>yesterday</c> only while it is the UTC day before <paramref name="now"/>,
    /// else the date (<c>on 2026-10-03</c>), so a file that was not renewed never reads as recent.
    /// </summary>
    public static string Day(DateOnly? asOf, DateTimeOffset now) =>
        asOf is not { } day ? "in the last full day"
        : day == DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1) ? "yesterday"
        : $"on {Date(day)}";

    /// <summary>
    /// The month the last number covers: <c>this month</c> while <paramref name="now"/> is in it (UTC), else its name
    /// (<c>in September</c>, on the first of a month and for an old file), with the year when that differs too.
    /// </summary>
    public static string Month(DateOnly? asOf, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return asOf is not { } day || (day.Year == today.Year && day.Month == today.Month) ? "this month"
            : day.Year == today.Year ? $"in {day.ToString("MMMM", CultureInfo.InvariantCulture)}"
            : $"in {day.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The three amounts with their days: <c>$1.52 yesterday</c>, <c>$9.80 in 7 days</c>, <c>$11.02 this month</c>.</summary>
    public static IReadOnlyList<CostPart> Parts(CostAmounts amounts, string? currency, DateOnly? asOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return
        [
            new CostPart(Money(amounts.Yesterday, currency), Day(asOf, now)),
            new CostPart(Money(amounts.Last7Days, currency), "in 7 days"),
            new CostPart(Money(amounts.MonthToDate, currency), Month(asOf, now)),
        ];
    }

    /// <summary><c>$1.52 yesterday · $9.80 in 7 days · $11.02 this month · as of 2026-10-06 (UTC)</c>.</summary>
    public static string Line(CostAmounts amounts, CostReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);
        var parts = Parts(amounts, report.Currency, report.AsOf, now).Select(part => $"{part.Amount} {part.Period}");
        return string.Join(" · ", AsOf(report.AsOf) is { } asOf ? parts.Append(asOf) : parts);
    }

    /// <summary>
    /// <c>as of 2026-10-06 (UTC)</c>: the last day the numbers include; null when the file does not say. The days of a
    /// cost are UTC days, on a page whose times are the viewer's: the line says so where it names the day.
    /// </summary>
    public static string? AsOf(DateOnly? asOf) => asOf is { } day ? $"as of {Date(day)} ({DayZone})" : null;

    /// <summary>The time zone of every day of a cost.</summary>
    public const string DayZone = "UTC";

    public static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>Most this month: Azure App Service $6.10, SQL Database $2.00</c>; null when the file names no service.
    /// </summary>
    public static string? Services(IReadOnlyList<CostService>? services, CostReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);
        return services is { Count: > 0 }
            ? $"Most {Month(report.AsOf, now)}: {string.Join(", ", services.Select(service => $"{service.Name} {Money(service.MonthToDate, report.Currency)}"))}"
            : null;
    }

    /// <summary>
    /// <c>plus about $2.69 this month of what the environments share (20 % of what all pods request)</c>; null when
    /// the estimate has no amount for the month.
    /// </summary>
    public static string? Estimate(CostEstimate? estimate, CostReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (estimate?.Amounts.MonthToDate is not { } amount)
        {
            return null;
        }

        var share = estimate.Share is { } part
            ? string.Create(CultureInfo.InvariantCulture, $" ({Math.Round(part * 100, MidpointRounding.AwayFromZero):0} % of what all pods request)")
            : string.Empty;
        return $"plus about {Money(amount, report.Currency)} {Month(report.AsOf, now)} of what the environments share{share}";
    }

    /// <summary>What an estimate is, for its tooltip.</summary>
    public const string EstimateHelp =
        "An estimate, not a bill: the cost of the cluster that no environment's tag claims, times the share of CPU and memory "
        + "this environment's pods request of what all running pods request now. It stays part of what the environments share.";

    /// <summary>What the numbers are and how old, for the tooltip of every cost line.</summary>
    public static string Help(CostReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var days = report.AsOf is { } day ? $"complete UTC days up to {Date(day)}" : "complete UTC days";
        return $"Not live: what Azure Cost Management reported for {days}, by the tag \"environment\" of the resources. "
            + "Azure's cost arrives hours late and is amended for a day or two; a workflow of the system repository reads it hourly until the day is complete, then every six hours.";
    }

    /// <summary>What the entry <see cref="CostReport.SharedName"/> holds.</summary>
    public const string SharedHelp =
        "What carries no tag \"environment\": the resources the environments share, such as the Front Door profile, the registry "
        + "and the Terraform state, and what Azure bills without tags.";

    /// <summary>What an entry is that belongs to no environment of the page and is not the shared one.</summary>
    public static string OtherHelp(string name) =>
        $"Azure bills resources with the tag \"environment\" = \"{name}\", which is no environment of this topology.";
}
