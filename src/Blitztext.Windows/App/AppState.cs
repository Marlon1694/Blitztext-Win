using System.ComponentModel;
using System.Windows;
using Blitztext.Core.AI;
using Blitztext.Core.History;
using Blitztext.Core.Settings;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;
using Blitztext.Speech;
using Blitztext.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blitztext.Windows.App;

public enum PopoverPage
{
    Main,
    Onboarding,
    Settings,
    Workflow,
    History,
    AudioFile,
    WhatsNew,
}

/// <summary>What the tray icon and the recording pill show.</summary>
public abstract record TrayStatus
{
    public sealed record Idle : TrayStatus;
    public sealed record Recording(WorkflowType Type) : TrayStatus;
    public sealed record Processing(WorkflowType Type) : TrayStatus;
    public sealed record Success(WorkflowType? Type) : TrayStatus;
    public sealed record Failure(WorkflowType? Type) : TrayStatus;

    public static readonly TrayStatus IdleStatus = new Idle();
}

/// <summary>
/// The app's state and behaviour: settings, the running dictation, shortcuts, pasting and
/// model downloads. The windows bind to it; it knows nothing about them.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private const int PasteRetryInitialAttempts = 22;

    private readonly SettingsStore _settingsStore;
    private readonly SettingsContainer _settings;
    private readonly SystemAudioMuter _audioMuter = new();
    private readonly SynchronizationContext _ui;
    private TargetApp? _activePasteTarget;
    private TargetApp? _lastPopoverPasteTarget;
    private WorkflowLaunchSource _activeLaunchSource = WorkflowLaunchSource.Manual;
    private FocusedField? _activeField;
    private CancellationTokenSource? _fieldRead;
    private CancellationTokenSource? _statusReset;
    private CancellationTokenSource? _workflowCleanup;
    private CancellationTokenSource? _whatsNew;
    private CancellationTokenSource? _mute;
    /// <summary>Mode chosen at key down per shortcut, so key up (hold mode) stops the same dictation.</summary>
    private readonly Dictionary<WorkflowType, WorkflowType> _shortcutModes = [];

    public HotkeyService Hotkeys { get; } = new();
    public ClipboardPasteService Clipboard { get; }
    public ModelStore Models { get; } = new(AppPaths.ModelsDirectory);
    public ModelInstaller Installer { get; }
    public LocalTranscriptionService Transcription { get; }
    public IApiKeyStore ApiKeys { get; } = new CredentialApiKeyStore();
    public AIClient AiClient { get; }
    public TextRewriter Rewriter { get; }
    public TranscriptHistoryStore History { get; }
    public UpdateService Updates { get; } = new();
    public AudioFileController AudioFile { get; }

    [ObservableProperty] private PopoverPage _page = PopoverPage.Main;
    [ObservableProperty] private bool _isPopoverShown;
    [ObservableProperty] private TrayStatus _status = TrayStatus.IdleStatus;
    [ObservableProperty] private DictationWorkflow? _activeWorkflow;
    [ObservableProperty] private double? _localModelDownloadProgress;
    [ObservableProperty] private string? _localModelDownloadStatusText;
    [ObservableProperty] private string? _localModelDownloadErrorText;
    /// <summary>Bumped when a key is saved or removed; the Credential Manager is not observable.</summary>
    [ObservableProperty] private int _aiKeyRevision;
    /// <summary>Bumped on every settings change, so bindings to computed values refresh.</summary>
    [ObservableProperty] private int _settingsRevision;
    [ObservableProperty] private string? _pasteHint;

    public event Action? ShowPopoverRequested;
    public event Action? DismissPopoverRequested;
    public event Action? ChooseAudioFileRequested;

    public AppState(SettingsStore settingsStore)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
        Clipboard = new ClipboardPasteService();
        Installer = new ModelInstaller(Models);
        Transcription = new LocalTranscriptionService(Models);
        AiClient = new AIClient(ApiKeys);
        Rewriter = new TextRewriter(AiClient, ApiKeys, () => _settings.AiProvider);
        History = new TranscriptHistoryStore(AppPaths.HistoryFile, new DpapiProtector(), _settings.App.HistoryRetention);
        AudioFile = new AudioFileController(this);

        if (string.IsNullOrWhiteSpace(_settings.App.SelectedLocalTranscriptionModelName)
            || LocalModelCatalog.Find(_settings.App.SelectedLocalTranscriptionModelName) is null)
        {
            // An installed model wins; otherwise the recommended one.
            _settings.App.SelectedLocalTranscriptionModelName =
                Models.InstalledModels().FirstOrDefault()?.Id ?? LocalModelCatalog.RecommendedModelId;
        }
        Hotkeys.UpdateHotkeys(_settings.App.ResolvedHotkeys());
        Hotkeys.HotkeyPressed += (_, e) => HandleHotkey(e);
        History.Changed += (_, _) => OnPropertyChanged(nameof(LastHistoryEntry));
        PrewarmLocalTranscriptionIfNeeded();
    }

    // MARK: - Settings

    public AppSettings Settings => _settings.App;
    public TranscriptionSettings TranscriptionSettings => _settings.Transcription;
    public TextImprovementSettings TextImprovementSettings => _settings.TextImprovement;
    public DampfAblassenSettings DampfAblassenSettings => _settings.DampfAblassen;
    public EmojiTextSettings EmojiTextSettings => _settings.EmojiText;

    /// <summary>The provider connected with the user's own API key or a local server; null for none.</summary>
    public AIProviderConfig? AiProvider
    {
        get => _settings.AiProvider;
        set
        {
            if (_settings.AiProvider == value) return;
            _settings.AiProvider = value;
            SaveSettings();
            OnPropertyChanged();
            RaiseComputed();
        }
    }

    /// <summary>Applies a change to the settings, saves them and reacts to what changed.</summary>
    public void UpdateSettings(Action<SettingsContainer> change)
    {
        var before = _settings.App.Clone();
        change(_settings);
        var after = _settings.App;
        SaveSettings();

        if (!before.CustomHotkeys.OrderBy(k => k.Key).SequenceEqual(after.CustomHotkeys.OrderBy(k => k.Key)))
        {
            Hotkeys.UpdateHotkeys(after.ResolvedHotkeys());
        }
        if (before.HistoryRetention != after.HistoryRetention)
        {
            History.UpdateRetention(after.HistoryRetention);
            OnPropertyChanged(nameof(LastHistoryEntry));
        }
        if (before.SelectedLocalTranscriptionModelName != after.SelectedLocalTranscriptionModelName)
        {
            PrewarmLocalTranscriptionIfNeeded();
        }
        RaiseComputed();
    }

    private void SaveSettings() => _settingsStore.Save(_settings);

    private void RaiseComputed()
    {
        SettingsRevision++;
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(TextImprovementSettings));
        OnPropertyChanged(nameof(DampfAblassenSettings));
        OnPropertyChanged(nameof(EmojiTextSettings));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelIsInstalled));
        OnPropertyChanged(nameof(CanRewrite));
        OnPropertyChanged(nameof(IsAiProviderReady));
        OnPropertyChanged(nameof(ProviderInUse));
    }

    partial void OnAiKeyRevisionChanged(int value) => RaiseComputed();

    // MARK: - Computed

    /// <summary>Ready once a local speech model is installed; transcription always runs on this PC.</summary>
    public bool IsConfigured => Models.InstalledModels().Count > 0;

    public LocalModelInfo SelectedModel => LocalModelCatalog.Resolve(Settings.SelectedLocalTranscriptionModelName);

    public bool SelectedModelIsInstalled => Models.IsInstalled(SelectedModel);

    public bool IsDownloadingLocalModel => LocalModelDownloadProgress is not null;

    partial void OnLocalModelDownloadProgressChanged(double? value) => OnPropertyChanged(nameof(IsDownloadingLocalModel));

    public bool IsAiProviderReady => ApiKeys.IsConfigured(AiProvider);

    /// <summary>Blitztext+, $%&amp;! and :) can run.</summary>
    public bool CanRewrite => IsAiProviderReady;

    /// <summary>Name of the provider the AI modes use, null when none is set up.</summary>
    public string? ProviderInUse => IsAiProviderReady ? AiProvider?.Provider.DisplayName() : null;

    public bool ProviderIsLocal => AiProvider is { Provider: AIProvider.Custom } config
        && Uri.TryCreate(config.EffectiveBaseUrl, UriKind.Absolute, out var url) && url.IsLoopback;

    public bool ShouldShowOnboarding => !IsConfigured && !Settings.HasSeenOnboarding;

    public TranscriptHistoryEntry? LastHistoryEntry => History.Entries.FirstOrDefault();

    public WorkflowPhase CurrentPhase => ActiveWorkflow?.Phase ?? WorkflowPhase.IdlePhase;

    public string DisplayName(WorkflowType type)
    {
        var custom = type switch
        {
            WorkflowType.TextImprover => TextImprovementSettings.CustomName,
            WorkflowType.DampfAblassen => DampfAblassenSettings.CustomName,
            WorkflowType.EmojiText => EmojiTextSettings.CustomName,
            _ => "",
        };
        return string.IsNullOrWhiteSpace(custom) ? type.DisplayName() : custom.Trim();
    }

    public string WorkflowSubtitle(WorkflowType type)
    {
        if (type == WorkflowType.TapTalk)
        {
            return SelectedModelIsInstalled ? $"Lokal: {SelectedModel.DisplayName}." : "Lokales Sprachmodell fehlt.";
        }
        return CanRewrite ? type.Subtitle() : "Braucht einen Modell-Anbieter (API-Key oder Ollama).";
    }

    public KeyCombo Hotkey(WorkflowType type) => Settings.Hotkey(type);

    public void SetCustomHotkey(KeyCombo? combo, WorkflowType type)
    {
        UpdateSettings(settings =>
        {
            var hotkeys = settings.App.CustomHotkeys;
            if (combo is not null)
            {
                // One shortcut can only start one mode: others with the same keys go back to their default.
                foreach (var key in hotkeys.Where(h => h.Value.SameKeys(combo) && h.Key != type.Key()).Select(h => h.Key).ToList())
                {
                    hotkeys.Remove(key);
                }
                if (combo.SameKeys(KeyCombo.Default(type))) hotkeys.Remove(type.Key());
                else hotkeys[type.Key()] = combo;
            }
            else
            {
                hotkeys.Remove(type.Key());
            }
        });
    }

    /// <summary>Modes whose built-in shortcut equals another mode's own shortcut lose it.</summary>
    public bool HotkeyConflicts(WorkflowType type) =>
        WorkflowTypes.All.Any(other => other != type && Hotkey(other).SameKeys(Hotkey(type)));

    public TranscriptPostProcessor PostProcessor => new()
    {
        CustomTerms = TextImprovementSettings.CustomTerms.ToList(),
        Replacements = TextImprovementSettings.Replacements.ToList(),
        CorrectCustomTerms = Settings.CorrectCustomTermsEnabled,
        RemoveFillerWords = Settings.RemoveFillerWordsEnabled,
        Language = TranscriptionSettings.Language,
        IsDictionaryWord = DictionaryWordChecker.Shared.IsKnownWord,
    };

    public string Instruction(WorkflowType type) => type switch
    {
        WorkflowType.TextImprover => ModePrompts.TextImprovement(TextImprovementSettings),
        WorkflowType.DampfAblassen => ModePrompts.DampfAblassen(DampfAblassenSettings),
        WorkflowType.EmojiText => ModePrompts.Emoji(EmojiTextSettings),
        _ => "",
    };

    public bool IsWorkflowAvailable(WorkflowType type) =>
        type == WorkflowType.TapTalk ? SelectedModelIsInstalled : SelectedModelIsInstalled && CanRewrite;

    // MARK: - Workflows

    public void StartWorkflow(WorkflowType type, WorkflowLaunchSource source = WorkflowLaunchSource.Manual, long press = 0)
    {
        if (!IsWorkflowAvailable(type))
        {
            if (source == WorkflowLaunchSource.Manual) Page = PopoverPage.Settings;
            return;
        }

        ActiveWorkflow?.Reset();
        _statusReset?.Cancel();
        _workflowCleanup?.Cancel();
        _activeLaunchSource = source;
        _activePasteTarget = source == WorkflowLaunchSource.HotkeyBackground ? TargetApp.CaptureForeground() : _lastPopoverPasteTarget;
        PasteHint = null;

        var workflow = new DictationWorkflow(type, new DictationContext(
            SelectedModel.Id,
            Settings.MicrophoneDeviceId,
            PostProcessor,
            Transcription,
            Rewriter,
            Instruction,
            () => Settings.AiSeesFieldContext ? _activeField?.AiContext : null))
        {
            StartedByPress = press,
        };
        workflow.Output += text => HandleWorkflowOutput(workflow, text);
        workflow.PhaseChanged += phase => HandleWorkflowPhaseChange(phase, workflow);
        ActiveWorkflow = workflow;
        ReadActiveField();
        workflow.Start();

        if (Settings.MuteOutputWhileRecording && workflow.IsRecording)
        {
            // A short delay: Strg+Win+→ (desktops) cancels within a moment and should not cut the sound.
            _mute?.Cancel();
            var mute = new CancellationTokenSource();
            _mute = mute;
            _ = Task.Delay(220, mute.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && ActiveWorkflow == workflow && workflow.IsRecording) _audioMuter.Mute();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        Page = source == WorkflowLaunchSource.Manual ? PopoverPage.Workflow : PopoverPage.Main;
    }

    /// <summary>
    /// The mode a shortcut starts in the app in front. Only the Blitztext shortcut follows
    /// "Modus je App"; the other shortcuts stay explicit. A rule whose AI mode cannot run
    /// falls back to plain dictation. Website rules are looked up afterwards, see <see cref="ApplyWebsiteRuleAsync"/>.
    /// </summary>
    public WorkflowType ModeForShortcut(WorkflowType type, TargetApp? app)
    {
        if (type != WorkflowType.TapTalk || app is null) return type;
        var rule = Settings.AppModeRules.FirstOrDefault(r =>
            string.Equals(r.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));
        return rule is not null && IsWorkflowAvailable(rule.Mode) ? rule.Mode : type;
    }

    /// <summary>
    /// Website rules need the browser's address, which takes a moment to read. The dictation
    /// starts right away and switches mode once the address is known; a website rule wins
    /// over the rule for its browser.
    /// </summary>
    private async Task ApplyWebsiteRuleAsync(DictationWorkflow workflow, TargetApp app)
    {
        if (Settings.WebsiteModeRules.Count == 0 || !FocusedFieldReader.IsBrowser(app.ProcessName)) return;
        var host = await FocusedFieldReader.WebHostAsync(app.Window, app.ProcessId, TimeSpan.FromMilliseconds(600));
        if (host is null || ActiveWorkflow != workflow) return;
        var rule = Settings.WebsiteModeRules
            .Where(r => WebsiteHost.Matches(host, r.Host))
            .OrderByDescending(r => r.Host.Length)
            .FirstOrDefault();
        if (rule is not null && IsWorkflowAvailable(rule.Mode))
        {
            workflow.Retarget(rule.Mode, explicitChoice: false);
        }
    }

    public void StopCurrentWorkflow() => ActiveWorkflow?.Stop();

    public void ResetCurrentWorkflow()
    {
        ActiveWorkflow?.Reset();
        _audioMuter.RestoreIfNeeded();
        ActiveWorkflow = null;
        _activePasteTarget = null;
        _activeLaunchSource = WorkflowLaunchSource.Manual;
        _statusReset?.Cancel();
        _workflowCleanup?.Cancel();
        Status = TrayStatus.IdleStatus;
        Page = PopoverPage.Main;
    }

    /// <summary>Gives the PC its sound and clipboard back, e.g. when Blitztext quits mid-recording.</summary>
    public void Shutdown()
    {
        _mute?.Cancel();
        _audioMuter.RestoreIfNeeded();
        ActiveWorkflow?.Reset();
        Clipboard.SettleNow();
        _settingsStore.Flush();
        Hotkeys.Dispose();
        Transcription.Dispose();
    }

    private void HandleWorkflowOutput(DictationWorkflow workflow, string dictated)
    {
        if (ActiveWorkflow != workflow) return;
        var text = dictated;
        // The AI modes shape their own paragraphs; word-for-word dictation gets them here.
        if (Settings.AutomaticParagraphs && workflow.Type == WorkflowType.TapTalk)
        {
            text = TranscriptParagraphService.Format(text, TranscriptionSettings.Language);
        }
        History.Add(text, workflow.Type);
        OnPropertyChanged(nameof(LastHistoryEntry));
        var output = text;
        if (Settings.FitToSurroundingText && _activeField is { } field)
        {
            output = InsertionFitter.Fit(text, field.TextBefore, field.TextAfter);
        }
        _ = PasteAtCursorAsync(output, _activePasteTarget);
        ScheduleWhatsNewAfterDictation();
        if (_activeLaunchSource == WorkflowLaunchSource.HotkeyBackground) Page = PopoverPage.Main;
        ScheduleWorkflowCleanup(workflow, TimeSpan.FromSeconds(1.05));
    }

    private void HandleWorkflowPhaseChange(WorkflowPhase phase, DictationWorkflow workflow)
    {
        if (ActiveWorkflow != workflow) return;
        _statusReset?.Cancel();
        // Sound comes back as soon as the recording ends, not after the transcription.
        if (!workflow.IsRecording)
        {
            _mute?.Cancel();
            _audioMuter.RestoreIfNeeded();
        }
        OnPropertyChanged(nameof(CurrentPhase));

        switch (phase)
        {
            case WorkflowPhase.Idle:
                Status = TrayStatus.IdleStatus;
                break;
            case WorkflowPhase.Running running:
                Status = workflow.IsRecording ? new TrayStatus.Recording(workflow.Type) : new TrayStatus.Processing(workflow.Type);
                // Shortcut users never see the window; a long model load would look like a hang.
                if (running.Message == LocalTranscriptionService.SlowPreparationMessage && !IsPopoverShown)
                {
                    Page = PopoverPage.Workflow;
                    ShowPopoverRequested?.Invoke();
                }
                break;
            case WorkflowPhase.Done:
                Status = new TrayStatus.Success(workflow.Type);
                break;
            case WorkflowPhase.Error error:
                Status = new TrayStatus.Failure(workflow.Type);
                Log.Info($"Dictation ended with an error: {error.Message}");
                if (_activeLaunchSource == WorkflowLaunchSource.HotkeyBackground)
                {
                    // Errors other than an empty recording get a visible explanation.
                    if (error.Message != DictationWorkflow.NothingRecognized && !IsPopoverShown)
                    {
                        Page = PopoverPage.Workflow;
                        ShowPopoverRequested?.Invoke();
                    }
                    else
                    {
                        ActiveWorkflow = null;
                        _activePasteTarget = null;
                        Page = PopoverPage.Main;
                    }
                }
                ScheduleStatusReset(TimeSpan.FromSeconds(1.6));
                break;
        }
    }

    private void ScheduleWorkflowCleanup(DictationWorkflow workflow, TimeSpan delay)
    {
        _workflowCleanup?.Cancel();
        var cancel = new CancellationTokenSource();
        _workflowCleanup = cancel;
        _ = Task.Delay(delay, cancel.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || ActiveWorkflow != workflow) return;
            workflow.Reset();
            ActiveWorkflow = null;
            _activePasteTarget = null;
            _activeLaunchSource = WorkflowLaunchSource.Manual;
            if (!IsPopoverShown) Page = PopoverPage.Main;
            Status = TrayStatus.IdleStatus;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ScheduleStatusReset(TimeSpan delay)
    {
        var cancel = new CancellationTokenSource();
        _statusReset = cancel;
        _ = Task.Delay(delay, cancel.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (ActiveWorkflow is null || !(ActiveWorkflow.Phase is WorkflowPhase.Running)) Status = TrayStatus.IdleStatus;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Reads the target field in the background while recording starts; nothing is read when no setting needs it.</summary>
    private void ReadActiveField()
    {
        _fieldRead?.Cancel();
        _activeField = null;
        if (!(Settings.FitToSurroundingText || Settings.AiSeesFieldContext) || _activePasteTarget is not { } target) return;
        var cancel = new CancellationTokenSource();
        _fieldRead = cancel;
        _ = FocusedFieldReader.ReadAsync(target.ProcessId, TimeSpan.FromMilliseconds(800)).ContinueWith(t =>
        {
            if (!cancel.IsCancellationRequested && t.IsCompletedSuccessfully) _activeField = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // MARK: - Shortcuts

    private void HandleHotkey(HotkeyEvent hotkeyEvent)
    {
        switch (hotkeyEvent.Kind)
        {
            case HotkeyEventKind.Down:
                HotkeyDown(hotkeyEvent);
                break;
            case HotkeyEventKind.Up:
                if (Settings.HotkeyMode != HotkeyMode.Hold) return;
                if (ActiveWorkflow is { } active && active.StartedByPress == hotkeyEvent.Press && active.IsRecording)
                {
                    active.Stop();
                }
                break;
            case HotkeyEventKind.Upgrade:
                if (ActiveWorkflow is { } upgraded && upgraded.StartedByPress == hotkeyEvent.Press && upgraded.IsRecording
                    && IsWorkflowAvailable(hotkeyEvent.Type))
                {
                    upgraded.Retarget(hotkeyEvent.Type, explicitChoice: true);
                }
                break;
            case HotkeyEventKind.Cancel:
                if (ActiveWorkflow is { } cancelled && cancelled.StartedByPress == hotkeyEvent.Press && cancelled.IsRecording)
                {
                    // Not a dictation but another shortcut, e.g. Strg+Win+→.
                    cancelled.Reset();
                    ResetCurrentWorkflow();
                }
                break;
            case HotkeyEventKind.Escape:
                if (ActiveWorkflow is { IsRecording: true } recording && Settings.HotkeyMode == HotkeyMode.Toggle)
                {
                    recording.Stop();
                }
                break;
        }
    }

    private void HotkeyDown(HotkeyEvent hotkeyEvent)
    {
        // In toggle mode, any mode's shortcut finishes the current recording.
        if (Settings.HotkeyMode == HotkeyMode.Toggle && ActiveWorkflow is { } active && active.Phase is WorkflowPhase.Running)
        {
            // A second stop during transcription would cancel its result.
            if (active.IsRecording) active.Stop();
            return;
        }

        var target = TargetApp.CaptureForeground();
        var mode = ModeForShortcut(hotkeyEvent.Type, target);
        _shortcutModes[hotkeyEvent.Type] = mode;

        if (!IsWorkflowAvailable(mode))
        {
            // Missing model or AI setup: say so instead of silently ignoring the shortcut.
            if (IsPopoverShown) return;
            PreparePopoverPresentation();
            if (Page != PopoverPage.WhatsNew) Page = PopoverPage.Main;
            ShowPopoverRequested?.Invoke();
            return;
        }

        StartWorkflow(mode, WorkflowLaunchSource.HotkeyBackground, hotkeyEvent.Press);
        if (hotkeyEvent.Type == WorkflowType.TapTalk && ActiveWorkflow is { } started && target is not null)
        {
            _ = ApplyWebsiteRuleAsync(started, target);
        }
    }

    // MARK: - Model download

    public async void InstallSelectedLocalModel()
    {
        if (IsDownloadingLocalModel) return;
        var model = SelectedModel;
        LocalModelDownloadProgress = 0;
        LocalModelDownloadStatusText = "Download startet ...";
        LocalModelDownloadErrorText = null;
        try
        {
            var progress = new Progress<double>(value =>
            {
                var clamped = Math.Clamp(value, 0, 1);
                LocalModelDownloadProgress = clamped;
                LocalModelDownloadStatusText = clamped >= 0.95 && model.ArchiveFolder is not null
                    ? "Wird entpackt und geprüft ..."
                    : $"Download {(int)(clamped * 100)} %";
            });
            await Installer.InstallAsync(model, progress);
            LocalModelDownloadProgress = null;
            LocalModelDownloadStatusText = $"{model.DisplayName} ist installiert.";
            LocalModelDownloadErrorText = null;
            UpdateSettings(s => s.App.SelectedLocalTranscriptionModelName = model.Id);
            _ = Transcription.PrepareAsync(model.Id).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (ModelInstallException ex)
        {
            LocalModelDownloadProgress = null;
            LocalModelDownloadStatusText = null;
            LocalModelDownloadErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("Model install failed: " + ex);
            LocalModelDownloadProgress = null;
            LocalModelDownloadStatusText = null;
            LocalModelDownloadErrorText = "Download fehlgeschlagen: " + ex.Message;
        }
        RaiseComputed();
    }

    public void RemoveModel(LocalModelInfo model)
    {
        Transcription.Unload();
        try
        {
            Models.Remove(model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalModelDownloadErrorText = "Modell konnte nicht gelöscht werden: " + ex.Message;
        }
        RaiseComputed();
    }

    private void PrewarmLocalTranscriptionIfNeeded()
    {
        if (!SelectedModelIsInstalled) return;
        var id = SelectedModel.Id;
        _ = Task.Run(async () =>
        {
            try { await Transcription.PrepareAsync(id); }
            catch (Exception ex) { Log.Error("Prewarm failed: " + ex.Message); }
        });
    }

    // MARK: - Clipboard and paste

    public void CopyToClipboard(string text) => Clipboard.Copy(text);

    public void PasteFromHistory(TranscriptHistoryEntry entry) => _ = PasteAtCursorAsync(entry.Text, _lastPopoverPasteTarget);

    /// <summary>
    /// Brings the target back when needed, then pastes with Ctrl+V. With
    /// <see cref="AppSettings.RestoreClipboardAfterPaste"/> the previous clipboard comes back once the
    /// target app has read the text. When pasting is impossible, the text stays on the clipboard.
    /// </summary>
    internal async Task PasteAtCursorAsync(string text, TargetApp? target)
    {
        if (IsPopoverShown)
        {
            // Still the foreground process: allowed to hand the focus back.
            target?.Activate();
            DismissPopoverRequested?.Invoke();
        }

        if (target is not null && ElevationCheck.IsElevated(target.ProcessId) && !ElevationCheck.IsCurrentProcessElevated)
        {
            // Windows blocks input into apps that run as administrator.
            CopyToClipboard(text);
            PasteHint = $"{target.AppName} läuft als Administrator. Der Text liegt in der Zwischenablage (Strg+V).";
            Status = new TrayStatus.Failure(ActiveWorkflow?.Type);
            return;
        }

        for (var attempts = PasteRetryInitialAttempts; attempts >= 0; attempts--)
        {
            var foreground = TargetApp.ForegroundProcessId();
            if (target is not null)
            {
                if (foreground == target.ProcessId)
                {
                    await PerformPasteAsync(text);
                    return;
                }
                target.Activate();
            }
            else if (foreground != 0 && foreground != (uint)Environment.ProcessId)
            {
                await PerformPasteAsync(text);
                return;
            }
            var delay = attempts switch { >= 16 => 15, >= 8 => 25, _ => 40 };
            await Task.Delay(delay);
        }
        // The target never came to the front: keep the text available for a manual paste.
        CopyToClipboard(text);
        PasteHint = "Der Text liegt in der Zwischenablage (Strg+V).";
    }

    private async Task PerformPasteAsync(string text)
    {
        // Win or Alt of the shortcut may still be down; Win+Strg+V would open the sound panel.
        await InputSender.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1.5));
        if (Settings.RestoreClipboardAfterPaste)
        {
            Clipboard.Paste(text, InputSender.SendPaste);
        }
        else
        {
            Clipboard.Copy(text);
            InputSender.SendPaste();
        }
    }

    // MARK: - Popover

    public void PreparePopoverPresentation()
    {
        _lastPopoverPasteTarget = TargetApp.CaptureForeground() ?? _lastPopoverPasteTarget;
        History.Prune();
        OnPropertyChanged(nameof(LastHistoryEntry));
        RaiseComputed();
        if (ActiveWorkflow is { } workflow && workflow.Phase.IsActive)
        {
            Page = PopoverPage.Workflow;
        }
        else if (ShouldShowOnboarding)
        {
            Page = PopoverPage.Onboarding;
            MarkOnboardingSeen();
        }
        else if (HasUnseenWhatsNew)
        {
            Page = PopoverPage.WhatsNew;
        }
        else if (Page is PopoverPage.Workflow or PopoverPage.Onboarding)
        {
            Page = PopoverPage.Main;
        }
    }

    public void PopoverClosed()
    {
        IsPopoverShown = false;
        switch (CurrentPhase)
        {
            case WorkflowPhase.Done or WorkflowPhase.Error:
                ResetCurrentWorkflow();
                break;
            default:
                // Keep a running file transcription visible for the next opening.
                if (Page == PopoverPage.AudioFile && AudioFile.IsWorking) break;
                if (ActiveWorkflow?.Phase.IsActive == true) break;
                Page = PopoverPage.Main;
                break;
        }
    }

    public void RequestChooseAudioFile() => ChooseAudioFileRequested?.Invoke();

    public void MarkOnboardingSeen()
    {
        if (Settings.HasSeenOnboarding) return;
        UpdateSettings(s => s.App.HasSeenOnboarding = true);
        // New installs get the onboarding, not the release note on top.
        MarkWhatsNewSeen();
    }

    // MARK: - What's new

    public bool HasUnseenWhatsNew => Settings.HasSeenOnboarding && Settings.LastSeenWhatsNewVersion != WhatsNew.Version;

    public void MarkWhatsNewSeen()
    {
        _whatsNew?.Cancel();
        if (Settings.LastSeenWhatsNewVersion == WhatsNew.Version) return;
        UpdateSettings(s => s.App.LastSeenWhatsNewVersion = WhatsNew.Version);
        OnPropertyChanged(nameof(HasUnseenWhatsNew));
    }

    /// <summary>
    /// People who only dictate via shortcut never open the window. Show the note once, after
    /// the first dictation has been pasted, unless the next one already started.
    /// </summary>
    private void ScheduleWhatsNewAfterDictation()
    {
        if (!HasUnseenWhatsNew || _activeLaunchSource != WorkflowLaunchSource.HotkeyBackground || _whatsNew is not null) return;
        var cancel = new CancellationTokenSource();
        _whatsNew = cancel;
        _ = Task.Delay(2500, cancel.Token).ContinueWith(t =>
        {
            _whatsNew = null;
            if (t.IsCanceled || !HasUnseenWhatsNew || IsPopoverShown || ActiveWorkflow?.Phase.IsActive == true) return;
            Page = PopoverPage.WhatsNew;
            ShowPopoverRequested?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // MARK: - Data removal

    /// <summary>Deletes settings, history, API keys and models; used before uninstalling.</summary>
    public string? DeleteAllLocalData()
    {
        try
        {
            foreach (var provider in AIProviders.All) ApiKeys.Delete(provider);
            History.Clear();
            Transcription.Unload();
            LaunchAtLoginService.SetEnabled(false);
            if (Directory.Exists(AppPaths.ModelsDirectory)) Directory.Delete(AppPaths.ModelsDirectory, recursive: true);
            if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
            AiKeyRevision++;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Nicht alles konnte gelöscht werden: " + ex.Message;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ActiveWorkflow)) OnPropertyChanged(nameof(CurrentPhase));
    }
}

/// <summary>What changed in this release. Shown once per <see cref="Version"/>.</summary>
public static class WhatsNew
{
    public const string Version = "1.0";

    public sealed record Item(string Glyph, string Title, string Detail);

    public static readonly Item[] Items =
    [
        new("", "Blitztext für Windows",
            "Strg + Win halten, sprechen, loslassen. Die Spracherkennung läuft auf diesem PC, der Text landet direkt am Cursor."),
        new("", "Aufnahme sichtbar",
            "Unten am Bildschirm zeigt eine kleine lila Anzeige, dass Blitztext zuhört. Sie bewegt sich mit deiner Stimme, ein Klick stoppt die Aufnahme."),
    ];
}
