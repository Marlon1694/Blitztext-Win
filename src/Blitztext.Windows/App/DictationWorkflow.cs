using Blitztext.Core.AI;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;
using Blitztext.Speech;
using Blitztext.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blitztext.Windows.App;

/// <summary>What a dictation needs from the app, captured when it starts.</summary>
public sealed record DictationContext(
    string ModelId,
    Microphone Microphone,
    string MicrophoneId,
    TranscriptPostProcessor PostProcessor,
    LocalTranscriptionService Transcription,
    TextRewriter Rewriter,
    Func<WorkflowType, string> Instruction,
    Func<string?> FieldContext);

/// <summary>
/// One dictation in any mode: record, transcribe locally, clean up and, for the AI modes,
/// rewrite. The mode can change while recording (a further modifier, a website rule).
/// </summary>
public sealed partial class DictationWorkflow : ObservableObject
{
    public const string RecordingMessage = "Aufnahme läuft ...";
    public const string TranscribingMessage = "Wird transkribiert ...";
    public const string NothingRecognized = "Keine Aufnahme erkannt.";
    public const string SilentMicrophone =
        "Das Mikrofon liefert keinen Ton. In Windows unter Einstellungen > Datenschutz und Sicherheit > Mikrofon den Zugriff für Desktop-Apps erlauben oder ein anderes Mikrofon wählen.";

    /// <summary>Recording goes on this long after the shortcut is released, so the last word is complete.</summary>
    public static readonly TimeSpan PostRoll = TimeSpan.FromMilliseconds(300);

    private readonly DictationContext _context;
    private readonly SynchronizationContext _ui;
    private MicrophoneRecording? _recording;
    private CancellationTokenSource? _processing;

    [ObservableProperty] private WorkflowType _type;
    [ObservableProperty] private WorkflowPhase _phase = WorkflowPhase.IdlePhase;

    /// <summary>The finished text, once per dictation.</summary>
    public event Action<string>? Output;
    public event Action<WorkflowPhase>? PhaseChanged;
    /// <summary>The microphone delivered its last audio, after the short tail behind the release.</summary>
    public event Action? CaptureEnded;

    public DictationWorkflow(WorkflowType type, DictationContext context)
    {
        _type = type;
        _context = context;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    /// <summary>Press number of the shortcut that started this dictation; 0 when started by click.</summary>
    public long StartedByPress { get; init; }

    /// <summary>True once the mode was chosen explicitly (another modifier), so rules no longer apply.</summary>
    public bool ModeIsExplicit { get; private set; }

    public bool IsRecording => _recording?.IsRecording ?? false;
    public float AudioLevel => _recording?.AudioLevel ?? 0;

    partial void OnPhaseChanged(WorkflowPhase value) => PhaseChanged?.Invoke(value);

    /// <param name="requestedAt">When the shortcut was pressed, to measure how fast the microphone answers.</param>
    public void Start(long requestedAt)
    {
        // The recording first: the phase change asks IsRecording for the tray status.
        _recording = _context.Microphone.Start(_context.MicrophoneId, requestedAt, error => _ui.Post(_ => OnMicrophoneEnded(error), null));
        Phase = new WorkflowPhase.Running(RecordingMessage);
    }

    private void OnMicrophoneEnded(string? error)
    {
        // After the release the result covers it.
        if (Phase is not WorkflowPhase.Running { Message: RecordingMessage }) return;
        if (error is not null) Phase = new WorkflowPhase.Error(error);
        // The microphone went away mid-dictation: write down what came so far.
        else if (IsRecording) Stop();
    }

    /// <summary>Changes the mode while the dictation has not reached its AI step.</summary>
    public void Retarget(WorkflowType type, bool explicitChoice)
    {
        if (!Phase.IsActive || Phase is WorkflowPhase.Done or WorkflowPhase.Error) return;
        if (!explicitChoice && ModeIsExplicit) return;
        ModeIsExplicit |= explicitChoice;
        if (Type == type) return;
        Type = type;
        // Lets the tray icon and the pill show the new mode.
        PhaseChanged?.Invoke(Phase);
    }

    public void Stop()
    {
        if (IsRecording)
        {
            _ = ProcessAsync();
        }
        else
        {
            _processing?.Cancel();
            Phase = WorkflowPhase.IdlePhase;
        }
    }

    public void Reset()
    {
        _processing?.Cancel();
        _recording?.Cancel();
        Phase = WorkflowPhase.IdlePhase;
    }

    private async Task ProcessAsync()
    {
        var cancel = new CancellationTokenSource();
        _processing = cancel;
        var recording = _recording!;
        var stopping = recording.StopAsync(PostRoll);
        Phase = new WorkflowPhase.Running(TranscribingMessage);
        var clip = await stopping;
        CaptureEnded?.Invoke();
        var duration = recording.Duration;
        if (cancel.IsCancellationRequested) return;
        Log.Info($"Recorded {duration:F1} s; audio starts "
            + (recording.StartDelay is { } delay
                ? delay >= TimeSpan.Zero ? $"{delay.TotalMilliseconds:F0} ms after the shortcut" : $"{-delay.TotalMilliseconds:F0} ms before the shortcut"
                : "nowhere: the microphone delivered nothing")
            + (recording.OpenedDevice ? " (microphone opened for this recording)" : ""));

        if (recording.ErrorMessage is { } error)
        {
            Phase = new WorkflowPhase.Error(error);
            return;
        }
        if (TranscriptionQualityService.ShouldRejectRecording(duration))
        {
            Phase = new WorkflowPhase.Error(NothingRecognized);
            return;
        }
        if (recording.WasSilent && duration >= 1)
        {
            Phase = new WorkflowPhase.Error(SilentMicrophone);
            return;
        }

        try
        {
            var raw = await _context.Transcription.TranscribeAsync(
                clip.Samples,
                _context.ModelId,
                onSlowPreparation: () => _ui.Post(_ =>
                {
                    if (!cancel.IsCancellationRequested) Phase = new WorkflowPhase.Running(LocalTranscriptionService.SlowPreparationMessage);
                }, null),
                cancellationToken: cancel.Token);
            if (cancel.IsCancellationRequested) return;

            var cleaned = TranscriptionQualityService.CleanedTranscript(raw);
            if (TranscriptionQualityService.IsLikelyArtifact(cleaned, duration))
            {
                Phase = new WorkflowPhase.Error(NothingRecognized);
                return;
            }

            // The spell checker behind the term correction runs on a background thread.
            var processed = await Task.Run(() => _context.PostProcessor.Process(cleaned), cancel.Token);
            if (processed.Length == 0)
            {
                Phase = new WorkflowPhase.Error(NothingRecognized);
                return;
            }
            if (cancel.IsCancellationRequested) return;

            var type = Type;
            var result = processed;
            if (type.IsAiMode())
            {
                Phase = new WorkflowPhase.Running(StepMessage(type));
                var instruction = ModePrompts.WithFieldContext(_context.Instruction(type), _context.FieldContext());
                var rewritten = await _context.Rewriter.RewriteAsync(processed, instruction, cancel.Token);
                result = TranscriptionQualityService.CleanedTranscript(rewritten);
                if (result.Length == 0 || result == ModePrompts.NothingRecognizedMarker)
                {
                    Phase = new WorkflowPhase.Error(NothingRecognized);
                    return;
                }
            }
            if (cancel.IsCancellationRequested) return;

            Phase = new WorkflowPhase.Done(result);
            Output?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (LocalTranscriptionException ex) when (ex.Message == LocalTranscriptionService.NoTextMessage)
        {
            // Noise or a short tap of the shortcut: the same quiet ending as an empty recording.
            if (!cancel.IsCancellationRequested) Phase = new WorkflowPhase.Error(NothingRecognized);
        }
        catch (Exception ex)
        {
            Log.Error($"{Type.Key()} failed: {ex.GetType().Name}: {ex.Message}");
            if (!cancel.IsCancellationRequested) Phase = new WorkflowPhase.Error(ex.Message);
        }
    }

    public static string StepMessage(WorkflowType type) => type switch
    {
        WorkflowType.TextImprover => "Text wird verbessert ...",
        WorkflowType.DampfAblassen => "Wird umformuliert ...",
        WorkflowType.EmojiText => "Emojis werden eingefügt ...",
        _ => TranscribingMessage,
    };
}
