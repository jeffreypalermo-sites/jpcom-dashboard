namespace Dashboard.Tests;

public class TrafficCountTests
{
    // A reading as js/traffic.js gives it: sent, answered, failed, unanswered, sending (1 or 0).
    private static TrafficCount Reading(params int[] status) => TrafficCount.From(status);

    [Fact]
    public void WhileRequestsAreSentTheLineCountsThemWithTheSecondsLeft()
    {
        var count = Reading(18, 18, 0, 0, 1);

        Assert.Equal((18, 18, 0, 0, true), (count.Sent, count.Answered, count.Failed, count.Unanswered, count.Sending));
        Assert.False(count.Over);
        Assert.Equal("18 sent, 18 answered; 51 s left.", count.Status(51));
    }

    [Fact]
    public void ARunWhoseAnswersAllArrivedIsDone()
    {
        var count = Reading(118, 118, 0, 0, 0);

        Assert.True(count.Over);
        Assert.Equal("Done: 118 sent, 118 answered.", count.Status(0));
        Assert.Equal("Traffic ended: 118 sent, 118 answered", count.Ended(stopped: false));
        Assert.Equal("Traffic stopped: 118 sent, 118 answered", count.Ended(stopped: true));
    }

    // The run of 2026-10-08 on tdd: the answers stopped arriving about 51 s into the run, the requests went on, and
    // the panel ended with "Done: 119 sent, 99 answered." although the app had received every request.
    [Fact]
    public void ARunIsNotOverWhileAnswersAreOnTheirWay()
    {
        var stalled = Reading(107, 99, 0, 0, 1);
        var allSent = Reading(119, 99, 0, 0, 0);

        Assert.Equal("107 sent, 99 answered, 8 on their way; 6 s left.", stalled.Status(6));
        Assert.Equal(20, allSent.OnTheirWay);
        Assert.False(allSent.Over);
        Assert.Equal("119 sent, 99 answered, 20 on their way; waiting for their answers.", allSent.Status(0));
    }

    [Fact]
    public void AnswersThatArriveAfterTheLastRequestAreCounted()
    {
        var late = Reading(119, 119, 0, 0, 0);

        Assert.True(late.Over);
        Assert.Equal("Done: 119 sent, 119 answered.", late.Status(0));
        Assert.Equal("Traffic ended: 119 sent, 119 answered", late.Ended(stopped: false));
    }

    [Fact]
    public void ARequestGivenUpIsSaidInWordsAndEndsTheWait()
    {
        Assert.Equal(NodeProber.DefaultTimeout, TimeSpan.FromSeconds(TrafficPlan.AnswerWithinSeconds));

        var waiting = Reading(119, 105, 0, 6, 0);
        var over = Reading(119, 109, 0, 10, 0);

        Assert.False(waiting.Over);
        Assert.Equal("119 sent, 105 answered, 6 with no answer within 10 s, 8 on their way; waiting for their answers.", waiting.Status(0));
        Assert.True(over.Over);
        Assert.Equal("Done: 119 sent, 109 answered, 10 with no answer within 10 s.", over.Status(0));
        Assert.Equal("Traffic ended: 119 sent, 109 answered, 10 with no answer within 10 s", over.Ended(stopped: false));
    }

    [Theory]
    // Sending and nothing on its way: not over, the next request comes.
    [InlineData(new[] { 0, 0, 0, 0, 1 }, 60, false, "0 sent, 0 answered; 60 s left.")]
    // One request without its answer yet, as most readings between two requests find it.
    [InlineData(new[] { 3, 2, 0, 0, 1 }, 59, false, "3 sent, 2 answered, 1 on its way; 59 s left.")]
    // A network failure is neither an answer nor on its way.
    [InlineData(new[] { 10, 7, 2, 0, 1 }, 55, false, "10 sent, 7 answered, 2 failed, 1 on its way; 55 s left.")]
    [InlineData(new[] { 120, 0, 120, 0, 0 }, 0, true, "Done: 120 sent, 0 answered, 120 failed.")]
    // Stopped by the viewer with one request on its way: its answer is waited for.
    [InlineData(new[] { 31, 30, 0, 0, 0 }, 44, false, "31 sent, 30 answered, 1 on its way; waiting for its answer.")]
    [InlineData(new[] { 31, 30, 1, 0, 0 }, 44, true, "Done: 31 sent, 30 answered, 1 failed.")]
    public void EveryRequestSentIsAnsweredFailedGivenUpOrOnItsWay(int[] status, int secondsLeft, bool over, string line)
    {
        var count = TrafficCount.From(status);

        Assert.Equal(count.Sent, count.Answered + count.Failed + count.Unanswered + count.OnTheirWay);
        Assert.Equal(over, count.Over);
        Assert.Equal(line, count.Status(secondsLeft));
    }

    [Fact]
    public void BeforeTheFirstPressNothingRunsAndAStartedRunIsNotOver()
    {
        Assert.True(TrafficCount.None.Over);
        Assert.False(TrafficCount.Started.Over);
        Assert.Equal("0 sent, 0 answered; 60 s left.", TrafficCount.Started.Status(60));
    }

    [Fact]
    public void AReadingHasFiveNumbers()
    {
        var error = Assert.Throws<ArgumentException>(() => TrafficCount.From([119, 99, 0, 1]));

        Assert.StartsWith("A reading of the traffic has five numbers, not 4.", error.Message, StringComparison.Ordinal);
    }
}
