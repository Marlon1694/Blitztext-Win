namespace Blitztext.Core.Paste;

/// <summary>
/// How long a paste keeps the transcript on the clipboard.
///
/// Restoring too early makes the target paste the old clipboard, restoring late costs almost
/// nothing (a quick manual Ctrl+V would paste the dictation once more). So every value leans
/// towards waiting longer.
/// </summary>
public sealed record ClipboardRestoreTimings
{
    /// <summary>
    /// Earliest restore after the paste shortcut, even if a read came in at once. The first
    /// read may come from a tool that watches the clipboard instead of from the target, and a
    /// target that reads twice gets the second copy without asking again, so Blitztext cannot
    /// see it. Nobody reaches for the old clipboard within a second.
    /// </summary>
    public double MinimumHold { get; init; } = 1.0;

    /// <summary>
    /// Quiet time after the last read. Repeated reads of one paste arrive within tens of
    /// milliseconds; half a second leaves an order of magnitude of headroom.
    /// </summary>
    public double QuietPeriod { get; init; } = 0.5;

    /// <summary>
    /// Without any read the paste most likely went nowhere (no text field had focus). Five
    /// seconds also leaves room for a target that is busy for a moment.
    /// </summary>
    public double NoReadTimeout { get; init; } = 5;

    /// <summary>Hard upper bound, so a reader that never stops cannot hold the clipboard forever.</summary>
    public double MaximumWait { get; init; } = 15;

    public static readonly ClipboardRestoreTimings Standard = new();
}

/// <summary>
/// Pure bookkeeping for one paste: restore now, keep waiting, or leave the clipboard alone.
/// Times are monotonic seconds passed in by the caller, so tests control the clock.
/// </summary>
public sealed class ClipboardRestoreTracker(double pastedAt, long ownChangeCount, ClipboardRestoreTimings? timings = null)
{
    public abstract record Decision
    {
        public sealed record Wait(double Until) : Decision;
        public sealed record RestoreNow : Decision;
        /// <summary>Someone else wrote to the clipboard; their content wins.</summary>
        public sealed record Skip : Decision;

        public static readonly Decision Restore = new RestoreNow();
        public static readonly Decision Skipped = new Skip();
    }

    public double PastedAt { get; } = pastedAt;
    /// <summary>Change count right after Blitztext wrote the transcript.</summary>
    public long OwnChangeCount { get; } = ownChangeCount;
    public ClipboardRestoreTimings Timings { get; } = timings ?? ClipboardRestoreTimings.Standard;
    public int ReadCount { get; private set; }
    public double? FirstReadAt { get; private set; }
    public double? LastReadAt { get; private set; }

    public void RecordRead(double time)
    {
        ReadCount++;
        FirstReadAt = Math.Min(FirstReadAt ?? time, time);
        LastReadAt = Math.Max(LastReadAt ?? time, time);
    }

    /// <summary>When the snapshot goes back if nobody else touches the clipboard.</summary>
    public double RestoreAt
    {
        get
        {
            var due = LastReadAt is { } lastRead
                ? Math.Max(lastRead + Timings.QuietPeriod, PastedAt + Timings.MinimumHold)
                : PastedAt + Timings.NoReadTimeout;
            return Math.Min(due, PastedAt + Timings.MaximumWait);
        }
    }

    public Decision DecisionAt(double now, long changeCount)
    {
        if (changeCount != OwnChangeCount) return Decision.Skipped;
        var due = RestoreAt;
        return now >= due ? Decision.Restore : new Decision.Wait(due);
    }

    /// <summary>Decision when the wait has to end now (new paste, quit): never <see cref="Decision.Wait"/>.</summary>
    public Decision SettledDecision(long changeCount) =>
        changeCount == OwnChangeCount ? Decision.Restore : Decision.Skipped;
}
