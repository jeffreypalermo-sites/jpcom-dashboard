using System.Globalization;

namespace Dashboard.Health;

/// <summary>Times as the dashboard shows them, in the time zone of the viewer.</summary>
public static class TimeText
{
    /// <summary>The time of day: <c>15:04:05</c>.</summary>
    public static string Clock(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Date and time to the minute with the offset from UTC: <c>2026-10-04 17:00 -05:00</c>.</summary>
    public static string DateAndTime(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    /// <summary>The machine-readable form for the <c>datetime</c> attribute of a <c>time</c> element.</summary>
    public static string Iso(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
