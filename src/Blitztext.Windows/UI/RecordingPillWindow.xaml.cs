using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Blitztext.Core.Workflows;
using Blitztext.Windows.App;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.UI;

/// <summary>
/// A small pill at the bottom of the screen while Blitztext records or writes, so the
/// recording is visible wherever you type. Lilac bars follow the voice; while recording, a
/// click stops it. The window never takes the focus, so the text still lands in the app you
/// are typing in.
/// </summary>
public partial class RecordingPillWindow : Window
{
    private readonly AppState _app;
    private TrayStatus _status = TrayStatus.IdleStatus;
    private int _transition;
    private bool _hiding;

    public RecordingPillWindow(AppState app)
    {
        InitializeComponent();
        _app = app;
        Bars.Level = () => _app.ActiveWorkflow?.AudioLevel ?? 0;
        app.PropertyChanged += OnAppChanged;
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Status)) Update(_app.Status);
        else if (e.PropertyName is nameof(AppState.Settings) or nameof(AppState.SettingsRevision)) Update(_status);
    }

    private void Update(TrayStatus status)
    {
        _status = status;
        if (!_app.Settings.ShowsRecordingIndicator)
        {
            HideNow();
            return;
        }
        switch (status)
        {
            case TrayStatus.Recording recording:
                ModeGlyph.Text = recording.Type.Glyph();
                ShowView(RecordingView);
                // A new recording picks the screen you work on now; Strg+Win+→ cancels within a moment.
                ShowAfter(placing: !IsVisible || _hiding, delay: IsVisible ? 0 : 120);
                break;
            case TrayStatus.Processing:
                ShowView(ProcessingView);
                ShowAfter(placing: !IsVisible, delay: 0);
                break;
            case TrayStatus.Success:
                if (!IsVisible || _hiding) return;
                ShowView(DoneView);
                HideAfter(TimeSpan.FromMilliseconds(900));
                break;
            default:
                HideAfter(TimeSpan.Zero);
                break;
        }
    }

    private void ShowView(FrameworkElement view)
    {
        RecordingView.Visibility = view == RecordingView ? Visibility.Visible : Visibility.Collapsed;
        ProcessingView.Visibility = view == ProcessingView ? Visibility.Visible : Visibility.Collapsed;
        DoneView.Visibility = view == DoneView ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ShowAfter(bool placing, int delay)
    {
        var current = ++_transition;
        _hiding = false;
        if (delay > 0)
        {
            await Task.Delay(delay);
            if (current != _transition) return;
        }
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            placing = true;
        }
        if (placing) Place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    private async void HideAfter(TimeSpan delay)
    {
        if (!IsVisible) return;
        var current = ++_transition;
        _hiding = true;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
            if (current != _transition) return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (current != _transition) return;
            _hiding = false;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void HideNow()
    {
        _transition++;
        _hiding = false;
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    /// <summary>Bottom centre, above the taskbar, of the screen with the pointer.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);
        var work = info.rcWork;
        var x = work.Left + (work.Right - work.Left - width) / 2;
        var y = work.Bottom - height - (int)(4 * scale);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>A tool window that is never activated, not even by a click.</summary>
    private void MakeNonActivating()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_status is TrayStatus.Recording && _app.ActiveWorkflow is { IsRecording: true } workflow)
        {
            workflow.Stop();
        }
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AppHost.IsQuitting) e.Cancel = true;
        base.OnClosing(e);
    }
}
