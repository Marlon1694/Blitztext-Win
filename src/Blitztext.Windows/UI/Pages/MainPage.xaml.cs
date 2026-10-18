using System.Windows;
using System.Windows.Controls;
using Blitztext.Core.Workflows;
using Blitztext.Windows.App;

namespace Blitztext.Windows.UI.Pages;

public partial class MainPage : UserControl
{
    private readonly MainViewModel _model;
    private CancellationTokenSource? _copied;

    public MainPage(AppState app)
    {
        InitializeComponent();
        _model = new MainViewModel(app);
        DataContext = _model;
    }

    private AppState App => _model.App;

    private void OnSettings(object sender, RoutedEventArgs e) => App.Page = PopoverPage.Settings;

    private void OnHistory(object sender, RoutedEventArgs e) => App.Page = PopoverPage.History;

    private void OnAudioFile(object sender, RoutedEventArgs e) => App.RequestChooseAudioFile();

    private void OnQuit(object sender, RoutedEventArgs e) => AppHost.Quit();

    private void OnDictate(object sender, RoutedEventArgs e) => App.StartWorkflow(WorkflowType.TapTalk);

    private void OnInstallModel(object sender, RoutedEventArgs e) => App.InstallSelectedLocalModel();

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (await App.Updates.InstallAsync()) AppHost.Quit();
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorkflowType type }) App.StartWorkflow(type);
    }

    private async void OnCopyLast(object sender, RoutedEventArgs e)
    {
        if (_model.LastEntry is not { } entry) return;
        App.CopyToClipboard(entry.Text);
        _copied?.Cancel();
        var cancel = new CancellationTokenSource();
        _copied = cancel;
        _model.CopiedLast = true;
        try
        {
            await Task.Delay(1600, cancel.Token);
            _model.CopiedLast = false;
        }
        catch (TaskCanceledException)
        {
        }
    }
}
