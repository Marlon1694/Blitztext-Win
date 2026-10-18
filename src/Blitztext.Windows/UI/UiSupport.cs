using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Blitztext.Windows.UI;

/// <summary>Attached properties the control templates read.</summary>
public static class Ext
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ext), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius value) => o.SetValue(CornerRadiusProperty, value);

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Ext), new FrameworkPropertyMetadata(Brushes.Transparent));

    public static Brush GetHoverBackground(DependencyObject o) => (Brush)o.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject o, Brush value) => o.SetValue(HoverBackgroundProperty, value);

    /// <summary>For the placeholder of a PasswordBox, whose Password is no dependency property.</summary>
    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached(
        "IsEmpty", typeof(bool), typeof(Ext), new FrameworkPropertyMetadata(true));

    public static bool GetIsEmpty(DependencyObject o) => (bool)o.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject o, bool value) => o.SetValue(IsEmptyProperty, value);
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible for non-null, non-empty values; <see cref="Invert"/> for the opposite.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var present = value is not null && !(value is string s && s.Length == 0);
        return present != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>An entry of a segmented control or a picker.</summary>
public sealed record Option(string Label, object Value);

/// <summary>Follows the Windows light or dark app mode.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(Application app) => ApplyForced(app, !AppsUseLightTheme());

    /// <summary>Light or dark regardless of the Windows setting, for screenshots.</summary>
    public static void ApplyForced(Application app, bool dark)
    {
        IsDark = dark;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "/Blitztext;component/Theme/Dark.xaml" : "/Blitztext;component/Theme/Light.xaml", UriKind.Relative),
        };
        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Insert(0, dictionary);
        _current = dictionary;
        Changed?.Invoke();
    }

    public static void Watch(Application app)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    var dark = !AppsUseLightTheme();
                    if (dark != IsDark) Apply(app);
                });
            }
        };
    }

    public static bool AppsUseLightTheme() => ReadPersonalize("AppsUseLightTheme");

    /// <summary>The taskbar has its own setting; the tray icon follows it.</summary>
    public static bool SystemUsesLightTheme() => ReadPersonalize("SystemUsesLightTheme");

    private static bool ReadPersonalize(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is not int value || value != 0;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
    }

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Text of relative times in German, like "vor 10 Minuten".</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset date, bool shortForm = false)
    {
        var span = DateTimeOffset.Now - date;
        if (span.TotalSeconds < 60) return "gerade eben";
        if (span.TotalMinutes < 60)
        {
            var minutes = (int)span.TotalMinutes;
            return shortForm ? $"vor {minutes} Min." : minutes == 1 ? "vor 1 Minute" : $"vor {minutes} Minuten";
        }
        if (span.TotalHours < 24)
        {
            var hours = (int)span.TotalHours;
            return shortForm ? $"vor {hours} Std." : hours == 1 ? "vor 1 Stunde" : $"vor {hours} Stunden";
        }
        var days = (int)span.TotalDays;
        return days == 1 ? "gestern" : $"vor {days} Tagen";
    }
}

/// <summary>The speech bubble of Blitztext's record button: a circle with a tail at the bottom left.</summary>
public sealed class SpeechBubble : Shape
{
    protected override Geometry DefiningGeometry
    {
        get
        {
            // Width and Height are always set; RenderSize is not known yet while measuring.
            var size = double.IsNaN(Width) || double.IsNaN(Height) ? 52 : Math.Min(Width, Height);
            var radius = size / 2;
            var circle = new EllipseGeometry(new Point(radius, radius), radius, radius);
            var tail = new StreamGeometry();
            using (var context = tail.Open())
            {
                context.BeginFigure(new Point(radius * 0.18, radius * 1.45), true, true);
                context.LineTo(new Point(-radius * 0.02, size + radius * 0.16), true, false);
                context.LineTo(new Point(radius * 0.72, size * 0.93), true, false);
            }
            return new CombinedGeometry(GeometryCombineMode.Union, circle, tail);
        }
    }
}

/// <summary>Bars that follow the input level and keep a soft motion of their own.</summary>
public sealed class VoiceBars : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(VoiceBars), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(VoiceBars), new FrameworkPropertyMetadata(7, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    /// <summary>Reads the current input level, 0...1.</summary>
    public Func<float>? Level { get; set; }

    private readonly DispatcherTimer _timer;
    private readonly DateTime _start = DateTime.UtcNow;
    private double _smoothed;

    public VoiceBars()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _timer.Start();
            else _timer.Stop();
        };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var count = Math.Max(1, BarCount);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var time = (DateTime.UtcNow - _start).TotalSeconds;
        _smoothed += ((Level?.Invoke() ?? 0) - _smoothed) * 0.35;
        var voice = Math.Min(1, _smoothed * 1.6);
        var barWidth = Math.Min(4, width / (count * 2.0));
        var gap = (width - count * barWidth) / Math.Max(1, count - 1);
        for (var index = 0; index < count; index++)
        {
            var shape = 0.55 + 0.45 * Math.Sin((index + 1) / (double)(count + 1) * Math.PI);
            var wobble = 0.5 + 0.5 * Math.Sin(time * 7 + index * 0.9);
            var barHeight = Math.Max(barWidth, height * (0.18 + 0.82 * shape * (0.25 * wobble + voice * (0.6 + 0.4 * wobble))));
            barHeight = Math.Min(height, barHeight);
            var x = index * (barWidth + gap);
            var rect = new Rect(x, (height - barHeight) / 2, barWidth, barHeight);
            drawingContext.DrawRoundedRectangle(BarBrush, null, rect, barWidth / 2, barWidth / 2);
        }
    }
}

/// <summary>Three dots that rise in turn, while Blitztext writes.</summary>
public sealed class TypingDots : FrameworkElement
{
    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(TypingDots), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private readonly DateTime _start = DateTime.UtcNow;

    public TypingDots()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _timer.Start();
            else _timer.Stop();
        };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var radius = Math.Min(width / 8, height / 4);
        var time = (DateTime.UtcNow - _start).TotalSeconds;
        for (var index = 0; index < 3; index++)
        {
            var phase = Math.Sin(time * 6 - index * 0.8);
            var lift = Math.Max(0, phase) * radius;
            var opacity = 0.45 + 0.55 * Math.Max(0, phase);
            var x = width / 2 + (index - 1) * radius * 2.8;
            drawingContext.PushOpacity(opacity);
            drawingContext.DrawEllipse(DotBrush, null, new Point(x, height / 2 - lift), radius, radius);
            drawingContext.Pop();
        }
    }
}

/// <summary>Speed or accuracy as a short bar, for the model comparison.</summary>
public sealed class RatingBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RatingBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var height = Math.Min(6, ActualHeight);
        var y = (ActualHeight - height) / 2;
        drawingContext.DrawRoundedRectangle(ThemeManager.Brush("Brush.Control"), null, new Rect(0, y, ActualWidth, height), height / 2, height / 2);
        var filled = Math.Clamp(Value, 0, 1) * ActualWidth;
        if (filled > 0)
        {
            drawingContext.DrawRoundedRectangle(ThemeManager.Brush("Brush.Accent"), null, new Rect(0, y, filled, height), height / 2, height / 2);
        }
    }
}
