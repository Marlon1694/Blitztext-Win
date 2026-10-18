using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Blitztext.Core.Paste;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.Services;

/// <summary>
/// Pastes text via the clipboard and afterwards restores what the user had copied before.
///
/// Approach: copy every format off the clipboard, then offer the transcript with delayed
/// rendering instead of as data. Windows asks Blitztext for the text the moment an app
/// actually reads it (WM_RENDERFORMAT), and that request is the signal that the paste landed.
/// The old clipboard goes back once reads have been quiet for a moment, or after a timeout if
/// nothing reads. If anything else writes to the clipboard in the meantime, the newer content
/// stays. <see cref="ClipboardRestoreTracker"/> makes the timing decisions.
/// </summary>
public sealed class ClipboardPasteService : IDisposable
{
    private sealed record Entry(uint Format, byte[] Data);

    private sealed class PendingPaste(int id, List<Entry> snapshot, string text, ClipboardRestoreTracker tracker)
    {
        public int Id { get; } = id;
        public List<Entry> Snapshot { get; } = snapshot;
        public string Text { get; } = text;
        public ClipboardRestoreTracker Tracker { get; } = tracker;
        /// <summary>Someone emptied the clipboard after Blitztext wrote the transcript.</summary>
        public bool Replaced { get; set; }
    }

    private const long Ours = 1;
    private const long Theirs = 2;

    private readonly HwndSource _window;
    private readonly ClipboardRestoreTimings _timings;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly uint _excludeFromMonitors;
    private readonly uint _canIncludeInHistory;
    private readonly uint _canUploadToCloud;
    private PendingPaste? _pending;
    private int _lastPasteId;
    private bool _writing;
    private CancellationTokenSource? _check;

    public ClipboardPasteService(ClipboardRestoreTimings? timings = null)
    {
        _timings = timings ?? ClipboardRestoreTimings.Standard;
        // A message-only window that owns the clipboard while the transcript is offered.
        _window = new HwndSource(new HwndSourceParameters("BlitztextClipboardOwner")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        // Public markers: Windows clipboard history, cloud clipboard and clipboard managers skip these items.
        _excludeFromMonitors = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _canUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Puts <paramref name="text"/> on the clipboard, runs <paramref name="sendPasteShortcut"/> and restores the previous clipboard afterwards.</summary>
    public void Paste(string text, Action sendPasteShortcut)
    {
        // The snapshot below must hold the user's content, not an earlier transcript.
        Settle();

        var snapshot = ReadSnapshot(out var listsContent);
        if (snapshot is null || (listsContent && snapshot.Count == 0))
        {
            // Content Blitztext cannot copy (e.g. only a bitmap handle): leave the transcript on the clipboard.
            Log.Info("Clipboard content cannot be saved; the transcript stays on the clipboard");
            SetText(text);
            sendPasteShortcut();
            return;
        }

        var id = ++_lastPasteId;
        var lazy = WriteLazily();
        if (!lazy)
        {
            Log.Info("Could not offer the transcript lazily; pasting plain text");
            SetText(text);
        }
        var pending = new PendingPaste(id, snapshot, text, new ClipboardRestoreTracker(Now, Ours, _timings));
        _pending = pending;

        sendPasteShortcut();
        Evaluate();
    }

    /// <summary>Puts text on the clipboard to stay there (copy buttons, failed paste).</summary>
    public void Copy(string text)
    {
        // A copy wins over a waiting restore.
        _pending = null;
        _check?.Cancel();
        SetText(text);
    }

    /// <summary>Settles a waiting paste right away; called when Blitztext quits.</summary>
    public void SettleNow() => Settle();

    private void Settle()
    {
        if (_pending is not { } pending) return;
        _check?.Cancel();
        Finish(pending, pending.Tracker.SettledDecision(ChangeToken(pending)));
    }

    private long ChangeToken(PendingPaste pending) =>
        pending.Replaced || GetClipboardOwner() != _window.Handle ? Theirs : Ours;

    private void Evaluate()
    {
        _check?.Cancel();
        if (_pending is not { } pending) return;
        var decision = pending.Tracker.DecisionAt(Now, ChangeToken(pending));
        if (decision is ClipboardRestoreTracker.Decision.Wait wait)
        {
            var cancel = new CancellationTokenSource();
            _check = cancel;
            var delay = TimeSpan.FromSeconds(Math.Max(0, wait.Until - Now) + 0.01);
            _ = Task.Delay(delay, cancel.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && _pending?.Id == pending.Id) Evaluate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        Finish(pending, decision);
    }

    private void Finish(PendingPaste pending, ClipboardRestoreTracker.Decision decision)
    {
        _pending = null;
        var elapsed = (int)((Now - pending.Tracker.PastedAt) * 1000);
        if (decision is not ClipboardRestoreTracker.Decision.RestoreNow)
        {
            Log.Info($"Clipboard changed while waiting; keeping the newer content ({elapsed} ms)");
            return;
        }
        if (!Restore(pending.Snapshot))
        {
            Log.Error("Could not write the previous clipboard back");
            return;
        }
        Log.Info($"Restored the previous clipboard after {elapsed} ms; {pending.Tracker.ReadCount} reads");
    }

    // MARK: - Window messages

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_RENDERFORMAT:
                handled = true;
                if (_pending is { } pending && (uint)wParam.ToInt64() == CF_UNICODETEXT)
                {
                    // The reading app holds the clipboard open; set the data without opening it.
                    var memory = AllocText(pending.Text);
                    if (memory != IntPtr.Zero && SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero) GlobalFree(memory);
                    pending.Tracker.RecordRead(Now);
                    Evaluate();
                }
                break;
            case WM_RENDERALLFORMATS:
                handled = true;
                if (_pending is { } owner && OpenWithRetry())
                {
                    try
                    {
                        if (GetClipboardOwner() == _window.Handle)
                        {
                            var memory = AllocText(owner.Text);
                            if (memory != IntPtr.Zero && SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero) GlobalFree(memory);
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                break;
            case WM_DESTROYCLIPBOARD:
                handled = true;
                if (!_writing && _pending is { } replaced) replaced.Replaced = true;
                break;
        }
        return IntPtr.Zero;
    }

    // MARK: - Clipboard access

    private bool OpenWithRetry()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (OpenClipboard(_window.Handle)) return true;
            Thread.Sleep(attempt < 4 ? 5 : 20);
        }
        return false;
    }

    /// <summary>Formats that are no plain memory block or that Windows derives itself.</summary>
    private static bool IsCopyable(uint format) => format switch
    {
        CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE or CF_OWNERDISPLAY
            or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE => false,
        >= CF_PRIVATEFIRST and <= CF_PRIVATELAST => false,
        >= CF_GDIOBJFIRST and <= CF_GDIOBJLAST => false,
        _ => true,
    };

    /// <summary>Every format the clipboard holds as memory, read eagerly. Null when the clipboard cannot be opened.</summary>
    private List<Entry>? ReadSnapshot(out bool listsContent)
    {
        listsContent = false;
        if (!OpenWithRetry()) return null;
        var entries = new List<Entry>();
        try
        {
            listsContent = CountClipboardFormats() > 0;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (!IsCopyable(format)) continue;
                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;
                var size = (long)GlobalSize(handle).ToUInt64();
                if (size <= 0) continue;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(pointer, data, 0, (int)size);
                    entries.Add(new Entry(format, data));
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }
        catch (Exception ex) when (ex is OverflowException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
        // Clipboards with only a bitmap or metafile would come back empty; don't touch them.
        if (listsContent && entries.Count == 0) return entries;
        return entries;
    }

    /// <summary>Offers the transcript with delayed rendering, marked as transient for clipboard history.</summary>
    private bool WriteLazily()
    {
        if (!OpenWithRetry()) return false;
        _writing = true;
        try
        {
            if (!EmptyClipboard()) return false;
            SetMarkers();
            // A null handle promises the data; Windows asks for it on the first read.
            SetClipboardData(CF_UNICODETEXT, IntPtr.Zero);
            return GetClipboardOwner() == _window.Handle;
        }
        finally
        {
            CloseClipboard();
            _writing = false;
        }
    }

    private void SetText(string text)
    {
        if (!OpenWithRetry()) return;
        _writing = true;
        try
        {
            EmptyClipboard();
            var memory = AllocText(text);
            if (memory != IntPtr.Zero && SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero) GlobalFree(memory);
        }
        finally
        {
            CloseClipboard();
            _writing = false;
        }
    }

    private bool Restore(List<Entry> snapshot)
    {
        if (!OpenWithRetry()) return false;
        _writing = true;
        try
        {
            EmptyClipboard();
            var hasExclusion = false;
            foreach (var entry in snapshot)
            {
                hasExclusion |= entry.Format == _excludeFromMonitors;
                var memory = Alloc(entry.Data);
                if (memory != IntPtr.Zero && SetClipboardData(entry.Format, memory) == IntPtr.Zero) GlobalFree(memory);
            }
            // The user's content is already in the clipboard history; don't add it twice.
            if (snapshot.Count > 0 && !hasExclusion) SetDword(_excludeFromMonitors, 0);
            return true;
        }
        finally
        {
            CloseClipboard();
            _writing = false;
        }
    }

    private void SetMarkers()
    {
        SetDword(_excludeFromMonitors, 0);
        SetDword(_canIncludeInHistory, 0);
        SetDword(_canUploadToCloud, 0);
    }

    private static void SetDword(uint format, int value)
    {
        if (format == 0) return;
        var memory = Alloc(BitConverter.GetBytes(value));
        if (memory != IntPtr.Zero && SetClipboardData(format, memory) == IntPtr.Zero) GlobalFree(memory);
    }

    private static IntPtr AllocText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return Alloc(bytes);
    }

    private static IntPtr Alloc(byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)Math.Max(1, data.Length));
        if (memory == IntPtr.Zero) return IntPtr.Zero;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(memory);
            return IntPtr.Zero;
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        return memory;
    }

    /// <summary>Plain text currently on the clipboard, for tests and diagnostics.</summary>
    public string? ReadText()
    {
        if (!OpenWithRetry()) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { GlobalUnlock(handle); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        Settle();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
