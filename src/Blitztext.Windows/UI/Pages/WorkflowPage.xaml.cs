using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Blitztext.Core.Workflows;
using Blitztext.Speech;
using Blitztext.Windows.App;
using Blitztext.Windows.Services;

namespace Blitztext.Windows.UI.Pages;

/// <summary>Recording, processing, result and error of the running mode; the same for all modes.</summary>
public partial class WorkflowPage : UserControl
{
    private readonly AppState _app;
    private DictationWorkflow? _workflow;

    public WorkflowPage(AppState app)
    {
        InitializeComponent();
        _app = app;
        Bars.Level = () => _workflow?.AudioLevel ?? 0;
        app.PropertyChanged += OnAppChanged;
        Attach(app.ActiveWorkflow);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ActiveWorkflow) or nameof(AppState.CurrentPhase) or nameof(AppState.Status))
        {
            Attach(_app.ActiveWorkflow);
        }
    }

    private void Attach(DictationWorkflow? workflow)
    {
        if (!ReferenceEquals(_workflow, workflow))
        {
            if (_workflow is not null) _workflow.PropertyChanged -= OnWorkflowChanged;
            _workflow = workflow;
            if (workflow is not null) workflow.PropertyChanged += OnWorkflowChanged;
        }
        Update();
    }

    private void OnWorkflowChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var workflow = _workflow;
        if (workflow is null)
        {
            Show(null);
            return;
        }
        var tint = ModeBrushes.Tint(workflow.Type);
        TitleText.Text = _app.DisplayName(workflow.Type);
        GlyphText.Text = workflow.Type.Glyph();
        StopBubble.Fill = tint;
        DoneBubble.Fill = tint;
        ProcessingBubble.Background = tint;
        Bars.BarBrush = tint;
        DoneTextBubble.Background = ModeBrushes.Soft(workflow.Type);

        switch (workflow.Phase)
        {
            case WorkflowPhase.Idle or WorkflowPhase.Running when workflow.IsRecording:
                Show(RecordingView);
                break;
            case WorkflowPhase.Running running:
                // Plain dictation only shows its message when the model prepares slowly; AI modes say which step runs.
                ProcessingText.Text = workflow.Type == WorkflowType.TapTalk && running.Message != LocalTranscriptionService.SlowPreparationMessage
                    ? "Wird transkribiert …"
                    : running.Message.Replace("...", "…");
                Show(ProcessingView);
                break;
            case WorkflowPhase.Done done:
                DoneText.Text = done.Text;
                Show(DoneView);
                break;
            case WorkflowPhase.Error error:
                ErrorText.Text = error.Message;
                MicrophoneButton.Visibility = error.Message.Contains("Mikrofon", StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                Show(ErrorView);
                break;
            default:
                Show(RecordingView);
                break;
        }
    }

    private void Show(FrameworkElement? view)
    {
        foreach (var candidate in new FrameworkElement[] { RecordingView, ProcessingView, DoneView, ErrorView })
        {
            candidate.Visibility = candidate == view ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => _app.ResetCurrentWorkflow();

    private void OnStop(object sender, RoutedEventArgs e) => _workflow?.Stop();

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        var type = _workflow?.Type ?? WorkflowType.TapTalk;
        _app.ResetCurrentWorkflow();
        _app.StartWorkflow(type);
    }

    private void OnMicrophoneSettings(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:privacy-microphone");
}
