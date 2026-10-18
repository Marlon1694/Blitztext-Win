using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Blitztext.Core.Workflows;
using Blitztext.Windows.App;

namespace Blitztext.Windows.UI.Pages;

public partial class AudioFilePage : UserControl
{
    private readonly AppState _app;
    private AudioFileController Controller => _app.AudioFile;

    public AudioFilePage(AppState app)
    {
        InitializeComponent();
        _app = app;
        DataContext = Controller;
        Controller.PropertyChanged += OnChanged;
        Update();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        WorkingView.Visibility = Controller.Phase == AudioFileController.FilePhase.Working ? Visibility.Visible : Visibility.Collapsed;
        FailedView.Visibility = Controller.Phase == AudioFileController.FilePhase.Failed ? Visibility.Visible : Visibility.Collapsed;
        DoneView.Visibility = Controller.Phase == AudioFileController.FilePhase.Done ? Visibility.Visible : Visibility.Collapsed;
        ModeText.Text = _app.DisplayName(Controller.Mode);
        RewriteButton.Visibility = _app.CanRewrite ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        Controller.Close();
        _app.Page = PopoverPage.Main;
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Controller.Copy();

    private void OnChoose(object sender, RoutedEventArgs e) => _app.RequestChooseAudioFile();

    private void OnRewriteMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RewriteButton, Placement = PlacementMode.Bottom };
        foreach (var mode in WorkflowTypes.AiModes.Where(m => m != Controller.Mode))
        {
            var item = new MenuItem { Header = _app.DisplayName(mode) };
            item.Click += (_, _) => Controller.Rewrite(mode);
            menu.Items.Add(item);
        }
        if (Controller.HasRewrite)
        {
            menu.Items.Add(new Separator());
            var original = new MenuItem { Header = "Original-Transkript" };
            original.Click += (_, _) => Controller.ShowTranscript();
            menu.Items.Add(original);
        }
        menu.IsOpen = true;
    }
}
