using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Blitztext.Core.Workflows;
using Blitztext.Windows.App;
using Forms = System.Windows.Forms;

namespace Blitztext.Windows.UI;

/// <summary>
/// The icon in the notification area: Blitztext's home, like the menu bar icon on the Mac.
/// A badge shows recording (red), writing (moving dot), done (check) and errors.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppState _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _animation;
    private readonly Bitmap _base;
    private IntPtr _currentHandle;
    private int _frame;
    private TrayStatus _status = TrayStatus.IdleStatus;

    public event Action? Clicked;
    public event Action? SettingsRequested;
    public event Action? HistoryRequested;
    public event Action? AudioFileRequested;
    public event Action? QuitRequested;

    public TrayIcon(AppState app)
    {
        _app = app;
        _base = LoadBaseIcon(Forms.SystemInformation.SmallIconSize.Width);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Blitztext öffnen", null, (_, _) => Clicked?.Invoke());
        menu.Items.Add("Audiodatei transkribieren …", null, (_, _) => AudioFileRequested?.Invoke());
        menu.Items.Add("Verlauf", null, (_, _) => HistoryRequested?.Invoke());
        menu.Items.Add("Einstellungen", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => QuitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
        };

        _animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _animation.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % 8;
            Render();
        };

        app.PropertyChanged += OnAppChanged;
        Render();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Status) or nameof(AppState.SettingsRevision))
        {
            _status = _app.Status;
            _frame = 0;
            if (_status is TrayStatus.Recording or TrayStatus.Processing) _animation.Start();
            else _animation.Stop();
            Render();
        }
    }

    private void Render()
    {
        var size = _base.Width;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(_base, 0, 0, size, size);
            DrawBadge(graphics, size);
        }
        var handle = bitmap.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
        _currentHandle = handle;
        _icon.Text = Tooltip();
    }

    private void DrawBadge(Graphics graphics, int size)
    {
        var badge = size * 0.5f;
        var rect = new RectangleF(size - badge - 0.5f, size - badge - 0.5f, badge, badge);
        switch (_status)
        {
            case TrayStatus.Recording:
            {
                var alpha = (int)(170 + 85 * Math.Sin(_frame / 8.0 * Math.PI * 2));
                using var ring = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
                using var fill = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), 229, 72, 77));
                graphics.FillEllipse(ring, Inflate(rect, 1));
                graphics.FillEllipse(fill, rect);
                break;
            }
            case TrayStatus.Processing:
            {
                using var ring = new SolidBrush(Color.White);
                using var fill = new SolidBrush(Color.FromArgb(255, 124, 116, 255));
                graphics.FillEllipse(ring, Inflate(rect, 1));
                graphics.FillEllipse(fill, rect);
                var angle = _frame / 8.0 * Math.PI * 2;
                var center = new PointF(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                var radius = rect.Width * 0.28f;
                var dot = rect.Width * 0.22f;
                using var white = new SolidBrush(Color.White);
                graphics.FillEllipse(white, center.X + radius * (float)Math.Cos(angle) - dot / 2,
                    center.Y + radius * (float)Math.Sin(angle) - dot / 2, dot, dot);
                break;
            }
            case TrayStatus.Success:
            {
                using var fill = new SolidBrush(Color.FromArgb(255, 30, 158, 87));
                graphics.FillEllipse(fill, rect);
                using var pen = new Pen(Color.White, Math.Max(1.2f, size / 14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLines(pen, new[]
                {
                    new PointF(rect.X + rect.Width * 0.27f, rect.Y + rect.Height * 0.52f),
                    new PointF(rect.X + rect.Width * 0.44f, rect.Y + rect.Height * 0.69f),
                    new PointF(rect.X + rect.Width * 0.74f, rect.Y + rect.Height * 0.34f),
                });
                break;
            }
            case TrayStatus.Failure:
            {
                using var fill = new SolidBrush(Color.FromArgb(255, 224, 102, 43));
                graphics.FillEllipse(fill, rect);
                using var pen = new Pen(Color.White, Math.Max(1.2f, size / 14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(pen, rect.X + rect.Width / 2, rect.Y + rect.Height * 0.25f, rect.X + rect.Width / 2, rect.Y + rect.Height * 0.55f);
                graphics.DrawLine(pen, rect.X + rect.Width / 2, rect.Y + rect.Height * 0.74f, rect.X + rect.Width / 2, rect.Y + rect.Height * 0.76f);
                break;
            }
            default:
                if (_app.HasUnseenWhatsNew || _app.Updates.UpdateAvailable)
                {
                    var dot = size * 0.32f;
                    using var ring = new SolidBrush(Color.White);
                    using var fill = new SolidBrush(Color.FromArgb(255, 229, 72, 77));
                    graphics.FillEllipse(ring, size - dot - 1, -0.5f, dot + 1, dot + 1);
                    graphics.FillEllipse(fill, size - dot - 0.5f, 0, dot, dot);
                }
                break;
        }
    }

    private static RectangleF Inflate(RectangleF rect, float amount) =>
        new(rect.X - amount, rect.Y - amount, rect.Width + 2 * amount, rect.Height + 2 * amount);

    private string Tooltip()
    {
        var text = _status switch
        {
            TrayStatus.Recording recording => $"{_app.DisplayName(recording.Type)}: Aufnahme läuft",
            TrayStatus.Processing processing => $"{_app.DisplayName(processing.Type)}: Verarbeitung läuft",
            TrayStatus.Success { Type: { } type } => $"{_app.DisplayName(type)}: Fertig",
            TrayStatus.Failure { Type: { } type } => $"{_app.DisplayName(type)}: Fehler",
            TrayStatus.Failure => "Blitztext: Fehler",
            _ => $"Blitztext ist bereit ({_app.Hotkey(WorkflowType.TapTalk).DisplayLabel})",
        };
        return text.Length > 120 ? text[..120] : text;
    }

    private static Bitmap LoadBaseIcon(int size)
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Blitztext.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var icon = new Icon(stream, new Size(size, size));
                return icon.ToBitmap();
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
        }
        return SystemIcons.Application.ToBitmap();
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _animation.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
        _base.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
