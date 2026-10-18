using Blitztext.Core.AI;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;
using Blitztext.Speech;
using Blitztext.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blitztext.Windows.App;

/// <summary>
/// Transcribes an audio file on this PC: voice memos (m4a), WhatsApp voice messages (Opus in
/// Ogg) and anything else Windows can decode. The result lands on the clipboard and in the
/// history; the AI modes can rewrite it afterwards.
/// </summary>
public sealed partial class AudioFileController(AppState appState) : ObservableObject
{
    public enum FilePhase
    {
        Idle,
        Working,
        Done,
        Failed,
    }

    [ObservableProperty] private FilePhase _phase = FilePhase.Idle;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private double _duration;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private WorkflowType _mode = WorkflowType.TapTalk;
    [ObservableProperty] private bool _copied;
    private string _transcript = "";
    private CancellationTokenSource? _task;

    public bool IsWorking => Phase == FilePhase.Working;

    public string DurationText
    {
        get
        {
            var seconds = (int)Math.Round(Duration);
            return seconds <= 0 ? "" : $"{seconds / 60}:{seconds % 60:00} min";
        }
    }

    partial void OnPhaseChanged(FilePhase value) => OnPropertyChanged(nameof(IsWorking));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));

    public async void Transcribe(string path)
    {
        _task?.Cancel();
        var cancel = new CancellationTokenSource();
        _task = cancel;
        FileName = Path.GetFileName(path);
        Duration = 0;
        _transcript = "";
        Text = "";
        Mode = WorkflowType.TapTalk;
        Copied = false;

        if (!appState.SelectedModelIsInstalled)
        {
            Fail("Zuerst in den Einstellungen ein lokales Sprachmodell laden.");
            return;
        }

        Work("Wird umgewandelt ...");
        var language = appState.TranscriptionSettings.Language;
        var modelId = appState.SelectedModel.Id;
        var postProcessor = appState.PostProcessor;
        var paragraphs = appState.Settings.AutomaticParagraphs;
        try
        {
            var clip = await Task.Run(() => AudioFileDecoder.Decode(path), cancel.Token);
            Duration = clip.Duration;
            if (cancel.IsCancellationRequested) return;

            Work("Wird transkribiert ...");
            var raw = await appState.Transcription.TranscribeAsync(
                clip.Samples,
                modelId,
                onSlowPreparation: () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!cancel.IsCancellationRequested) Work(LocalTranscriptionService.SlowPreparationMessage);
                }),
                cancellationToken: cancel.Token);
            var processed = await Task.Run(() =>
            {
                var result = postProcessor.Process(TranscriptionQualityService.CleanedTranscript(raw));
                return paragraphs ? TranscriptParagraphService.Format(result, language) : result;
            }, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            if (processed.Length == 0)
            {
                Fail("In der Datei wurde keine Sprache erkannt.");
                return;
            }
            _transcript = processed;
            Finish(processed, WorkflowType.TapTalk);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested) Fail(ex.Message);
        }
    }

    public async void Rewrite(WorkflowType newMode)
    {
        if (_transcript.Length == 0 || !newMode.IsAiMode()) return;
        _task?.Cancel();
        var cancel = new CancellationTokenSource();
        _task = cancel;
        Copied = false;
        Work($"{appState.DisplayName(newMode)} wird formuliert ...");
        try
        {
            var rewritten = await appState.Rewriter.RewriteAsync(_transcript, appState.Instruction(newMode), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Finish(TranscriptionQualityService.CleanedTranscript(rewritten), newMode);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested) Fail(ex.Message);
        }
    }

    public void ShowTranscript()
    {
        if (_transcript.Length == 0) return;
        Text = _transcript;
        Mode = WorkflowType.TapTalk;
        Copy();
    }

    public bool HasRewrite => Mode != WorkflowType.TapTalk;

    partial void OnModeChanged(WorkflowType value) => OnPropertyChanged(nameof(HasRewrite));

    public void Copy()
    {
        appState.CopyToClipboard(Text);
        Copied = true;
    }

    public void Close()
    {
        _task?.Cancel();
        _task = null;
        Phase = FilePhase.Idle;
    }

    private void Work(string message)
    {
        Message = message;
        Phase = FilePhase.Working;
    }

    private void Fail(string message)
    {
        Message = message;
        Phase = FilePhase.Failed;
    }

    private void Finish(string result, WorkflowType mode)
    {
        Text = result;
        Mode = mode;
        appState.History.Add(result, mode);
        Copy();
        Phase = FilePhase.Done;
    }
}
