using Blitztext.Core.Paste;
using static Blitztext.Core.Paste.ClipboardRestoreTracker;

namespace Blitztext.Core.Tests;

/// <summary>Timing rules for restoring the clipboard, with the clock under test control.</summary>
public class ClipboardRestoreTrackerTests
{
    private const long Own = 42;
    private static readonly ClipboardRestoreTimings Timings = ClipboardRestoreTimings.Standard;

    private static ClipboardRestoreTracker Tracker(double pastedAt = 100) => new(pastedAt, Own, Timings);

    [Fact]
    public void DefaultTimingsLeanTowardsWaiting()
    {
        Assert.True(Timings.QuietPeriod > 0);
        Assert.True(Timings.MinimumHold > Timings.QuietPeriod);
        Assert.True(Timings.NoReadTimeout > Timings.MinimumHold);
        Assert.True(Timings.MaximumWait > Timings.NoReadTimeout);
    }

    [Fact]
    public void WithoutReadsWaitsForTheTimeout()
    {
        var tracker = Tracker();
        Assert.Equal(new Decision.Wait(105), tracker.DecisionAt(100, Own));
        Assert.Equal(new Decision.Wait(105), tracker.DecisionAt(104.99, Own));
        Assert.Equal(Decision.Restore, tracker.DecisionAt(105, Own));
    }

    [Fact]
    public void RestoresAfterQuietPeriodFollowingARead()
    {
        var tracker = Tracker();
        tracker.RecordRead(101.5);
        Assert.Equal(new Decision.Wait(102), tracker.DecisionAt(101.6, Own));
        Assert.Equal(Decision.Restore, tracker.DecisionAt(102, Own));
    }

    [Fact]
    public void FastReadStillWaitsForTheMinimumHold()
    {
        var tracker = Tracker();
        tracker.RecordRead(100.02);
        Assert.Equal(new Decision.Wait(101), tracker.DecisionAt(100.6, Own));
        Assert.Equal(Decision.Restore, tracker.DecisionAt(101, Own));
    }

    [Fact]
    public void EveryReadRestartsTheQuietPeriod()
    {
        var tracker = Tracker();
        tracker.RecordRead(101.25);
        tracker.RecordRead(101.5);
        tracker.RecordRead(101.75);
        Assert.Equal(new Decision.Wait(102.25), tracker.DecisionAt(102, Own));
        Assert.Equal(3, tracker.ReadCount);
        Assert.Equal(101.25, tracker.FirstReadAt);
    }

    [Fact]
    public void ReadsArrivingOutOfOrderKeepTheLatest()
    {
        var tracker = Tracker();
        tracker.RecordRead(101.75);
        tracker.RecordRead(101.25);
        Assert.Equal(102.25, tracker.RestoreAt);
        Assert.Equal(101.25, tracker.FirstReadAt);
    }

    [Fact]
    public void ReadJustBeforeTheTimeoutStillGetsItsQuietPeriod()
    {
        var tracker = Tracker();
        tracker.RecordRead(104.875);
        Assert.Equal(new Decision.Wait(105.375), tracker.DecisionAt(105, Own));
        Assert.Equal(Decision.Restore, tracker.DecisionAt(105.375, Own));
    }

    [Fact]
    public void MaximumWaitEndsReadsThatNeverStop()
    {
        var tracker = Tracker();
        for (var time = 100.3; time < 120; time += 0.3) tracker.RecordRead(time);
        Assert.Equal(115, tracker.RestoreAt);
        Assert.Equal(Decision.Restore, tracker.DecisionAt(115, Own));
    }

    [Fact]
    public void NewerClipboardContentIsLeftAlone()
    {
        var tracker = Tracker();
        Assert.Equal(Decision.Skipped, tracker.DecisionAt(100.1, Own + 1));
        tracker.RecordRead(100.05);
        Assert.Equal(Decision.Skipped, tracker.DecisionAt(110, Own + 1));
    }

    [Fact]
    public void SettlingRestoresOnlyWhileTheClipboardIsStillOurs()
    {
        var tracker = Tracker();
        Assert.Equal(Decision.Restore, tracker.SettledDecision(Own));
        Assert.Equal(Decision.Skipped, tracker.SettledDecision(Own + 3));
    }
}
