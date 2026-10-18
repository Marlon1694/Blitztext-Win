using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Blitztext.Windows.App;
using Blitztext.Windows.UI.Pages;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.UI;

/// <summary>
/// The window that opens from the tray icon, like the menu bar popover on the Mac. It closes
/// when it loses the focus, unless a dialog of its own is open.
/// </summary>
public partial class PopoverWindow : Window
{
    private readonly AppState _app;
    private readonly Dictionary<PopoverPage, FrameworkElement> _pages = [];
    private POINT? _anchor;

    public PopoverWindow(AppState app)
    {
        InitializeComponent();
        _app = app;
        app.PropertyChanged += OnAppChanged;
        ShowPage(app.Page);
        Deactivated += (_, _) =>
        {
            if (!AppHost.AutoHideSuppressed && IsVisible) Dismiss();
        };
        PreviewKeyDown += OnKeyDown;
        SizeChanged += (_, _) => Reposition();
        SourceInitialized += (_, _) => ApplyWindowStyle();
        ThemeManager.Changed += ApplyWindowStyle;
    }

    public FrameworkElement Page(PopoverPage page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        FrameworkElement created = page switch
        {
            PopoverPage.Onboarding => new OnboardingPage(_app),
            PopoverPage.Settings => new SettingsPage(_app),
            PopoverPage.Workflow => new WorkflowPage(_app),
            PopoverPage.History => new HistoryPage(_app),
            PopoverPage.AudioFile => new AudioFilePage(_app),
            PopoverPage.WhatsNew => new WhatsNewPage(_app),
            _ => new MainPage(_app),
        };
        _pages[page] = created;
        return created;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Page)) ShowPage(_app.Page);
    }

    private void ShowPage(PopoverPage page)
    {
        var view = Page(page);
        if (!ReferenceEquals(PageHost.Content, view)) PageHost.Content = view;
    }

    /// <summary>Opens near the tray icon (or where the pointer is) and takes the focus.</summary>
    public void Present(bool atCursor)
    {
        _app.IsPopoverShown = true;
        if (GetCursorPos(out var point)) _anchor = atCursor ? point : null;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        Reposition();
        Opacity = 1;
        Activate();
        Focus();
    }

    public void Dismiss()
    {
        if (!IsVisible) return;
        Hide();
        _app.PopoverClosed();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_app.Page is PopoverPage.Main) Dismiss();
        else if (_app.Page == PopoverPage.Workflow) _app.ResetCurrentWorkflow();
        else _app.Page = PopoverPage.Main;
        e.Handled = true;
    }

    /// <summary>
    /// Bottom right above the taskbar, next to the tray, on the screen with the pointer. Works in
    /// physical pixels, so screens with different scaling are no problem.
    /// </summary>
    private void Reposition()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        GetCursorPos(out var cursor);
        var anchor = _anchor ?? cursor;
        var monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var scale = 1.0;
        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0) scale = dpiX / 96.0;

        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var work = info.rcWork;
        var screen = info.rcMonitor;
        const int gap = 4;

        int x, y;
        if (_anchor is null)
        {
            // Opened by a shortcut: bottom right of the work area, where the tray is by default.
            x = work.Right - width - gap;
            y = work.Bottom - height - gap;
        }
        else if (anchor.Y >= work.Bottom || work.Bottom < screen.Bottom && anchor.Y > work.Bottom - 8)
        {
            // Taskbar at the bottom (the usual case).
            x = anchor.X - width / 2;
            y = work.Bottom - height - gap;
        }
        else if (anchor.Y <= work.Top)
        {
            x = anchor.X - width / 2;
            y = work.Top + gap;
        }
        else if (anchor.X <= work.Left)
        {
            x = work.Left + gap;
            y = anchor.Y - height / 2;
        }
        else if (anchor.X >= work.Right)
        {
            x = work.Right - width - gap;
            y = anchor.Y - height / 2;
        }
        else
        {
            // The pointer is inside the work area: open above it.
            x = anchor.X - width / 2;
            y = anchor.Y - height - gap;
        }
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void ApplyWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides the window; Blitztext keeps running in the tray.
        if (!AppHost.IsQuitting)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }
}
