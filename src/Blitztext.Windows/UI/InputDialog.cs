using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Blitztext.Windows.UI;

/// <summary>A small dialog with one text field, styled like the rest of Blitztext.</summary>
public static class InputDialog
{
    public static string? Ask(Window? owner, string title, string message, string placeholder)
    {
        var field = new TextBox { Tag = placeholder, Margin = new Thickness(0, 12, 0, 16) };
        var ok = new Button { Content = "Hinzufügen", IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Style = Resource<Style>("Button.Filled") };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, Style = Resource<Style>("Button.Subtle") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, Style = Resource<Style>("Text.Headline"), FontSize = 15 });
        panel.Children.Add(new TextBlock { Text = message, Style = Resource<Style>("Text.Caption"), Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(field);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Resource<System.Windows.Media.Brush>("Brush.Page"),
            FontFamily = Resource<System.Windows.Media.FontFamily>("Font.Text"),
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => Keyboard.Focus(field);
        return window.ShowDialog() == true ? field.Text : null;
    }

    private static T Resource<T>(string key) where T : class => (T)Application.Current.FindResource(key);
}
