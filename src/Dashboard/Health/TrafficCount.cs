using System.Globalization;

namespace Dashboard.Health;

/// <summary>
/// What the traffic button counted at one reading (<c>status</c> of <c>js/traffic.js</c>). Every request sent is in
/// one of four places: answered, failed (the browser reports a network failure), given up with no answer within
/// <see cref="TrafficPlan.AnswerWithinSeconds"/> s, or still on its way. A run is over when nothing more is sent and
/// nothing is on its way, not when the sending stops: an answer that arrives after the last request was sent is
/// counted like any other, so a slow answer never reads as a lost one.
/// </summary>
/// <param name="Unanswered">Requests given up with no answer in time.</param>
/// <param name="Sending">True while requests are still being sent: until the run's seconds are over or it is stopped.</param>
public sealed record TrafficCount(int Sent, int Answered, int Failed, int Unanswered, bool Sending)
{
    /// <summary>Before the first press: nothing sent, nothing running.</summary>
    public static readonly TrafficCount None = new(0, 0, 0, 0, Sending: false);

    /// <summary>A run that was just started, before its first reading.</summary>
    public static readonly TrafficCount Started = None with { Sending = true };

    /// <summary>Requests that have neither an answer nor a failure yet, and were not given up.</summary>
    public int OnTheirWay => Math.Max(0, Sent - Answered - Failed - Unanswered);

    /// <summary>True when nothing more is sent and every request sent is accounted for.</summary>
    public bool Over => !Sending && OnTheirWay == 0;

    /// <summary>
    /// The numbers, which add up to the first: <c>119 sent, 99 answered, 20 on their way</c>. What is zero is left
    /// out, except the answered.
    /// </summary>
    public string Counts
    {
        get
        {
            var parts = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"{Sent} sent"),
                string.Create(CultureInfo.InvariantCulture, $"{Answered} answered"),
            };
            if (Failed > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Failed} failed"));
            }

            if (Unanswered > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Unanswered} with no answer within {TrafficPlan.AnswerWithinSeconds} s"));
            }

            if (OnTheirWay > 0)
            {
                parts.Add(OnTheirWay == 1 ? "1 on its way" : string.Create(CultureInfo.InvariantCulture, $"{OnTheirWay} on their way"));
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The line next to the button: the numbers with the seconds left while requests are sent, then what the panel
    /// waits for, then <c>Done: 119 sent, 119 answered.</c>
    /// </summary>
    public string Status(int secondsLeft) =>
        Over ? $"Done: {Counts}."
        : Sending ? string.Create(CultureInfo.InvariantCulture, $"{Counts}; {secondsLeft} s left.")
        : $"{Counts}; waiting for {(OnTheirWay == 1 ? "its answer" : "their answers")}.";

    /// <summary>The event at the end of a run: <c>Traffic ended: 119 sent, 119 answered</c>.</summary>
    public string Ended(bool stopped) => $"Traffic {(stopped ? "stopped" : "ended")}: {Counts}";

    /// <summary>
    /// A reading of <c>status</c>: sent, answered, failed, given up with no answer in time, and 1 while requests are
    /// still being sent.
    /// </summary>
    public static TrafficCount From(IReadOnlyList<int> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Count == 5
            ? new TrafficCount(status[0], status[1], status[2], status[3], status[4] != 0)
            : throw new ArgumentException($"A reading of the traffic has five numbers, not {status.Count}.", nameof(status));
    }
}
