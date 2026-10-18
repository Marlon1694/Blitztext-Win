using System.Windows;
using System.Windows.Controls;
using Blitztext.Core.History;
using Blitztext.Windows.App;

namespace Blitztext.Windows.UI.Pages;

public partial class HistoryPage : UserControl
{
    private readonly HistoryViewModel _model;

    public HistoryPage(AppState app)
    {
        InitializeComponent();
        _model = new HistoryViewModel(app);
        DataContext = _model;
        app.History.Changed += (_, _) => _model.Refresh();
    }

    private AppState App => _model.App;

    private void OnBack(object sender, RoutedEventArgs e) => App.Page = PopoverPage.Main;

    private static TranscriptHistoryEntry? Entry(object sender) => (sender as FrameworkElement)?.Tag as TranscriptHistoryEntry;

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (Entry(sender) is { } entry) App.PasteFromHistory(entry);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Entry(sender) is not { } entry) return;
        App.CopyToClipboard(entry.Text);
        if (sender is Button { Content: StackPanel { Children.Count: 2 } panel } && panel.Children[1] is TextBlock label)
        {
            label.Text = "Kopiert";
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Entry(sender) is { } entry) App.History.Remove(entry);
    }

    private void OnClear(object sender, RoutedEventArgs e) => App.History.Clear();
}
