using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using Blitztext.Core.AI;
using Blitztext.Core.History;
using Blitztext.Core.Settings;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;
using Blitztext.Speech;
using Blitztext.Windows.App;
using Blitztext.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blitztext.Windows.UI;

/// <summary>Refreshes all its bindings whenever the app state changes; the pages are small.</summary>
public abstract class PageModel : ObservableObject
{
    protected PageModel(AppState app)
    {
        App = app;
        app.PropertyChanged += OnAppChanged;
    }

    public AppState App { get; }

    protected virtual void OnAppChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    public virtual void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// Keeps the same list instance while the content is unchanged, so a refresh does not reset
    /// pickers bound to it.
    /// </summary>
    protected static IReadOnlyList<T> Stable<T>(ref IReadOnlyList<T>? cache, IReadOnlyList<T> fresh)
    {
        if (cache is not null && cache.SequenceEqual(fresh)) return cache;
        cache = fresh;
        return fresh;
    }
}

public sealed record ModeRow(WorkflowType Type, string Name, string Subtitle, IReadOnlyList<string> Keys, bool Enabled, Brush Tint, Brush TintSoft, string Glyph);

public sealed record ModelOption(string Id, string Label);

public static class ModeBrushes
{
    public static SolidColorBrush Tint(WorkflowType type)
    {
        var rgb = type.AccentColor();
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Soft(WorkflowType type)
    {
        var rgb = type.AccentColor();
        var brush = new SolidColorBrush(Color.FromArgb(0x26, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}

public sealed class MainViewModel(AppState app) : PageModel(app)
{
    public IReadOnlyList<ModeRow> Modes => WorkflowTypes.All.Select(type => new ModeRow(
        type,
        App.DisplayName(type),
        App.WorkflowSubtitle(type),
        App.Hotkey(type).Parts,
        App.IsWorkflowAvailable(type),
        ModeBrushes.Tint(type),
        ModeBrushes.Soft(type),
        type.Glyph())).ToList();

    public bool ShowSetupHero => !App.IsConfigured;

    public string DictationTitle
    {
        get
        {
            var keys = App.Hotkey(WorkflowType.TapTalk).DisplayLabel;
            return App.Settings.HotkeyMode == HotkeyMode.Hold ? $"{keys} halten und sprechen" : $"{keys} drücken und sprechen";
        }
    }

    public string DictationSubtitle
    {
        get
        {
            if (App.IsDownloadingLocalModel) return App.LocalModelDownloadStatusText ?? "Lokales Modell wird geladen.";
            return App.SelectedModelIsInstalled
                ? "Der Text landet direkt am Cursor."
                : $"{App.SelectedModel.DisplayName} ist noch nicht installiert.";
        }
    }

    public bool CanDictate => App.IsWorkflowAvailable(WorkflowType.TapTalk);

    private IReadOnlyList<ModelOption>? _modelOptions;

    public IReadOnlyList<ModelOption> ModelOptions => Stable(ref _modelOptions, LocalModelCatalog.All
        .Select(m => new ModelOption(m.Id, App.Models.IsInstalled(m) ? m.DisplayName : $"{m.DisplayName} (nicht installiert)"))
        .ToList());

    public string SelectedModelId
    {
        get => App.SelectedModel.Id;
        set
        {
            if (value is null || value == App.SelectedModel.Id) return;
            App.UpdateSettings(s => s.App.SelectedLocalTranscriptionModelName = value);
        }
    }

    public bool CanChangeModel => !App.IsDownloadingLocalModel;

    public bool ShowInstallButton => !App.SelectedModelIsInstalled && !App.IsDownloadingLocalModel;

    public string InstallButtonText => $"{App.SelectedModel.DisplayName} installieren ({App.SelectedModel.Rating.SizeLabel})";

    public double DownloadProgress => App.LocalModelDownloadProgress ?? 0;

    public bool IsDownloading => App.IsDownloadingLocalModel;

    public string? DownloadError => App.LocalModelDownloadErrorText;

    public bool StatusIsLocal => App.ProviderInUse is null || App.ProviderIsLocal;

    /// <summary>Where the speech and the AI modes run right now.</summary>
    public string StatusText
    {
        get
        {
            if (App.ProviderInUse is { } provider)
            {
                return App.ProviderIsLocal
                    ? "Sprache und KI-Modi bleiben auf diesem PC."
                    : $"Sprache bleibt auf diesem PC, KI-Modi über {provider}.";
            }
            return "Sprache bleibt auf diesem PC. KI-Modi brauchen einen Modell-Anbieter.";
        }
    }

    public TranscriptHistoryEntry? LastEntry => App.LastHistoryEntry;

    private bool _copiedLast;

    public bool CopiedLast
    {
        get => _copiedLast;
        set => SetProperty(ref _copiedLast, value);
    }

    public string LastEntryCaption => LastEntry is { } entry
        ? $"{App.DisplayName(entry.Workflow)}, {RelativeTime.Format(entry.CreatedAt)}"
        : "";

    public Brush LastEntryTint => LastEntry is { } entry ? ModeBrushes.Soft(entry.Workflow) : Brushes.Transparent;

    public string VersionText => $"v{UpdateService.CurrentVersionText}";

    public string? PasteHint => App.PasteHint;

    public bool ShowUpdate => App.Updates.UpdateAvailable;

    public string UpdateText => $"Version {App.Updates.AvailableVersion} ist verfügbar.";
}

public sealed record HistoryRow(TranscriptHistoryEntry Entry, string Header, string Text, string Glyph);

public sealed class HistoryViewModel(AppState app) : PageModel(app)
{
    public IReadOnlyList<HistoryRow> Entries => App.History.Entries
        .Select(e => new HistoryRow(e, $"{App.DisplayName(e.Workflow)} · {RelativeTime.Format(e.CreatedAt, shortForm: true)}", e.Text, e.Workflow.Glyph()))
        .ToList();

    public bool IsOff => App.Settings.HistoryRetention == HistoryRetention.Off;

    public bool IsEmpty => !IsOff && App.History.Entries.Count == 0;

    public bool HasEntries => !IsOff && App.History.Entries.Count > 0;

    public string RetentionHint => App.Settings.HistoryRetention switch
    {
        HistoryRetention.Day => "24 Stunden",
        HistoryRetention.Week => "7 Tage",
        _ => "",
    };

    public string EmptyDetail => $"Ergebnisse bleiben {RetentionHint} nur auf diesem PC, verschlüsselt.";

    public string Footer => $"Nur auf diesem PC, {RetentionHint}.";
}

public sealed record HotkeyRow(WorkflowType Type, string Name, string Current, bool IsCustom, bool HasConflict);

public sealed record RuleRow(object Rule, string Name, string Glyph, WorkflowType Mode);

public sealed class SettingsViewModel : PageModel
{
    private string _keyInput = "";
    private string? _testResult;
    private bool _testOk;
    private bool _testing;
    private string? _cleanupText;
    private string? _launchError;

    public SettingsViewModel(AppState app) : base(app)
    {
        app.Updates.PropertyChanged += (_, _) => Refresh();
    }

    // MARK: Pipeline overview

    public string SpeechModelName => App.SelectedModel.DisplayName;

    public string SpeechDetail => App.SelectedModelIsInstalled
        ? "Immer lokal auf diesem PC. Die Sprache verlässt ihn nie."
        : "Noch nicht installiert. Unter Modelle, Spracherkennung laden.";

    public bool SpeechReady => App.SelectedModelIsInstalled;

    public string AiModelName => App.ProviderInUse is { } provider
        ? $"{provider} · {App.AiProvider?.EffectiveModel}"
        : "Nicht eingerichtet";

    public string AiDetail
    {
        get
        {
            if (App.ProviderInUse is null)
            {
                return "Unter Modelle einen Modell-Anbieter mit API-Key hinterlegen, oder einen lokalen Server wie Ollama. Wortgenaues Diktieren geht auch ohne.";
            }
            if (App.ProviderIsLocal) return "Lokal auf diesem PC, ohne Internet.";
            // Must match the setting: with cursor context on, that text goes along too.
            return App.Settings.AiSeesFieldContext
                ? "Über deinen API-Key. Der erkannte Text und der Text am Cursor gehen an den Anbieter, nie die Sprache."
                : "Über deinen API-Key. Nur der erkannte Text geht an den Anbieter, nie die Sprache.";
        }
    }

    public bool AiReady => App.CanRewrite;

    // MARK: Shortcuts

    public IReadOnlyList<HotkeyRow> Hotkeys => WorkflowTypes.All.Select(type => new HotkeyRow(
        type,
        App.DisplayName(type),
        App.Hotkey(type).DisplayLabel,
        App.Settings.CustomHotkey(type) is not null,
        App.HotkeyConflicts(type))).ToList();

    public static IReadOnlyList<Option> HotkeyModes { get; } =
        [new Option(HotkeyMode.Hold.DisplayName(), HotkeyMode.Hold), new Option(HotkeyMode.Toggle.DisplayName(), HotkeyMode.Toggle)];

    public HotkeyMode HotkeyMode
    {
        get => App.Settings.HotkeyMode;
        set => App.UpdateSettings(s => s.App.HotkeyMode = value);
    }

    public string HotkeyModeDescription => App.Settings.HotkeyMode.Description();

    // MARK: Recording and pasting

    private IReadOnlyList<Option>? _microphones;

    /// <summary>Read when the settings open, not on every refresh: enumerating devices takes a moment.</summary>
    public IReadOnlyList<Option> Microphones => _microphones ??= LoadMicrophones();

    public void ReloadMicrophones()
    {
        var fresh = LoadMicrophones();
        Stable(ref _microphones, fresh);
        OnPropertyChanged(nameof(Microphones));
        OnPropertyChanged(nameof(MicrophoneId));
    }

    private static IReadOnlyList<Option> LoadMicrophones() =>
        new[] { new Option("Windows-Standard", "") }
            .Concat(Microphone.Devices().Select(m => new Option(m.Name, m.Id)))
            .ToList();

    public string MicrophoneId
    {
        get => App.Settings.MicrophoneDeviceId;
        set
        {
            if ((value ?? "") == App.Settings.MicrophoneDeviceId) return;
            App.UpdateSettings(s => s.App.MicrophoneDeviceId = value ?? "");
        }
    }

    public bool ShowsRecordingIndicator
    {
        get => App.Settings.ShowsRecordingIndicator;
        set => App.UpdateSettings(s => s.App.ShowsRecordingIndicator = value);
    }

    public static IReadOnlyList<Option> StandbyOptions { get; } =
    [
        new Option("Aus", MicrophoneStandby.Off),
        new Option("1 Minute", MicrophoneStandby.AfterDictation),
        new Option("Immer", MicrophoneStandby.Always),
    ];

    public MicrophoneStandby MicrophoneStandby
    {
        get => App.Settings.MicrophoneStandby;
        set => App.UpdateSettings(s => s.App.MicrophoneStandby = value);
    }

    public bool MuteOutputWhileRecording
    {
        get => App.Settings.MuteOutputWhileRecording;
        set => App.UpdateSettings(s => s.App.MuteOutputWhileRecording = value);
    }

    public bool RestoreClipboardAfterPaste
    {
        get => App.Settings.RestoreClipboardAfterPaste;
        set => App.UpdateSettings(s => s.App.RestoreClipboardAfterPaste = value);
    }

    public bool AutomaticParagraphs
    {
        get => App.Settings.AutomaticParagraphs;
        set => App.UpdateSettings(s => s.App.AutomaticParagraphs = value);
    }

    public bool FitToSurroundingText
    {
        get => App.Settings.FitToSurroundingText;
        set => App.UpdateSettings(s => s.App.FitToSurroundingText = value);
    }

    public static IReadOnlyList<Option> RetentionOptions { get; } =
    [
        new Option(HistoryRetention.Off.DisplayName(), HistoryRetention.Off),
        new Option(HistoryRetention.Day.DisplayName(), HistoryRetention.Day),
        new Option(HistoryRetention.Week.DisplayName(), HistoryRetention.Week),
    ];

    public HistoryRetention HistoryRetention
    {
        get => App.Settings.HistoryRetention;
        set => App.UpdateSettings(s => s.App.HistoryRetention = value);
    }

    // MARK: Launch at login

    public bool LaunchAtLogin
    {
        get => LaunchAtLoginService.IsEnabled;
        set
        {
            _launchError = LaunchAtLoginService.SetEnabled(value);
            Refresh();
        }
    }

    public string LaunchAtLoginText => _launchError ?? "Blitztext startet mit Windows und wartet im Infobereich der Taskleiste.";

    // MARK: About

    public string VersionText => $"Version {UpdateService.CurrentVersionText}";

    public bool CheckForUpdates
    {
        get => App.Settings.CheckForUpdates;
        set
        {
            App.UpdateSettings(s => s.App.CheckForUpdates = value);
            if (value) _ = App.Updates.CheckAsync(userInitiated: false);
        }
    }

    public string UpdateStatus => App.Updates.StatusText;

    public bool UpdateAvailable => App.Updates.UpdateAvailable;

    public bool IsBusyWithUpdate => App.Updates.IsChecking || App.Updates.IsInstalling;

    public string? CleanupText
    {
        get => _cleanupText;
        set
        {
            _cleanupText = value;
            Refresh();
        }
    }

    // MARK: Models

    private IReadOnlyList<ModelOption>? _modelOptions;

    public IReadOnlyList<ModelOption> ModelOptions => Stable(ref _modelOptions, LocalModelCatalog.All
        .Select(m => new ModelOption(m.Id, App.Models.IsInstalled(m) ? m.DisplayName : $"{m.DisplayName} (nicht installiert)"))
        .ToList());

    public string SelectedModelId
    {
        get => App.SelectedModel.Id;
        set
        {
            if (value is null || value == App.SelectedModel.Id) return;
            App.UpdateSettings(s => s.App.SelectedLocalTranscriptionModelName = value);
        }
    }

    public LocalModelInfo SelectedModel => App.SelectedModel;

    public string InstalledModelsText
    {
        get
        {
            var count = App.Models.InstalledModels().Count;
            if (!App.SelectedModelIsInstalled) return "Das ausgewählte Modell wird beim Installieren auf diesem PC gespeichert.";
            return count == 1 ? "1 lokales Sprachmodell installiert." : $"{count} lokale Sprachmodelle installiert.";
        }
    }

    public bool SelectedModelInstalled => App.SelectedModelIsInstalled;

    public bool IsDownloading => App.IsDownloadingLocalModel;

    public bool ShowInstallButton => !App.SelectedModelIsInstalled && !App.IsDownloadingLocalModel;

    public bool ShowRemoveButton => App.SelectedModelIsInstalled && !App.IsDownloadingLocalModel;

    public string InstallButtonText => $"{App.SelectedModel.DisplayName} installieren ({App.SelectedModel.Rating.SizeLabel})";

    public double DownloadProgress => App.LocalModelDownloadProgress ?? 0;

    public string? DownloadStatus => App.LocalModelDownloadStatusText;

    public string? DownloadError => App.LocalModelDownloadErrorText;

    public bool AiSeesFieldContext
    {
        get => App.Settings.AiSeesFieldContext;
        set => App.UpdateSettings(s => s.App.AiSeesFieldContext = value);
    }

    // MARK: Provider

    public static IReadOnlyList<Option> Providers { get; } =
        new[] { new Option("Keiner", "") }
            .Concat(AIProviders.All.Select(p => new Option(p.DisplayName(), p.Key())))
            .ToList();

    public string ProviderKey
    {
        get => App.AiProvider?.Provider.Key() ?? "";
        set
        {
            if ((value ?? "") == ProviderKey) return;
            var provider = AIProviders.All.FirstOrDefault(p => p.Key() == value);
            _keyInput = "";
            _testResult = null;
            App.AiProvider = string.IsNullOrEmpty(value) ? null : new AIProviderConfig(provider);
            Refresh();
        }
    }

    public bool HasProvider => App.AiProvider is not null;

    public bool IsCustomProvider => App.AiProvider?.Provider == AIProvider.Custom;

    public string KeyPlaceholder => App.AiProvider is { } config
        ? App.ApiKeys.MaskedKey(config.Provider) ?? (config.Provider.RequiresApiKey() ? "API-Key einfügen" : "API-Key (optional)")
        : "";

    public string KeyInput
    {
        get => _keyInput;
        set => SetProperty(ref _keyInput, value);
    }

    public bool HasSavedKey => App.AiProvider is { } config && App.ApiKeys.Get(config.Provider) is not null;

    public string? KeyHelpUrl => App.AiProvider?.Provider.KeyHelpUrl();

    public string ProviderModel
    {
        get => App.AiProvider?.Model ?? "";
        set
        {
            if (App.AiProvider is { } config && config.Model != (value ?? "").Trim()) App.AiProvider = config with { Model = (value ?? "").Trim() };
        }
    }

    public string ProviderModelPlaceholder => App.AiProvider?.Provider.DefaultModel() ?? "";

    public string ProviderBaseUrl
    {
        get => App.AiProvider?.BaseUrl ?? "";
        set
        {
            if (App.AiProvider is { } config && config.BaseUrl != (value ?? "").Trim()) App.AiProvider = config with { BaseUrl = (value ?? "").Trim() };
        }
    }

    public string ProviderBaseUrlPlaceholder => App.AiProvider?.Provider.DefaultBaseUrl() ?? "";

    public string ProviderNote => App.AiProvider is { } config
        ? config.Provider == AIProvider.Custom
            ? "Die Texte der KI-Modi gehen an diese Adresse. Mit Ollama oder LM Studio auf diesem PC bleibt alles lokal. Die Sprache selbst bleibt immer auf diesem PC."
            : $"Ab jetzt gehen die Texte der KI-Modi an {config.Provider.DisplayName()}. Die Sprache selbst bleibt auf diesem PC. Es gelten die Bedingungen und Kosten des Anbieters. Der Key liegt in der Windows-Anmeldeinformationsverwaltung."
        : "Optional. Blitztext+, $%&! und :) brauchen ein Sprachmodell: einen Anbieter mit eigenem API-Key oder einen lokalen Server wie Ollama oder LM Studio.";

    public bool Testing
    {
        get => _testing;
        private set => SetProperty(ref _testing, value);
    }

    public string? TestResult => _testResult;

    public bool TestOk => _testOk;

    public void SaveKey()
    {
        if (App.AiProvider is not { } config) return;
        App.ApiKeys.Save(config.Provider, _keyInput);
        _keyInput = "";
        _testResult = null;
        App.AiKeyRevision++;
        Refresh();
    }

    public void RemoveKey()
    {
        if (App.AiProvider is not { } config) return;
        App.ApiKeys.Delete(config.Provider);
        _testResult = null;
        App.AiKeyRevision++;
        Refresh();
    }

    public async void TestConnection()
    {
        if (App.AiProvider is not { } config) return;
        Testing = true;
        _testResult = null;
        Refresh();
        try
        {
            await App.AiClient.TestConnectionAsync(config);
            _testOk = true;
            _testResult = "Funktioniert";
        }
        catch (Exception ex)
        {
            _testOk = false;
            _testResult = ex.Message;
        }
        Testing = false;
        Refresh();
    }

    // MARK: Modes

    public string ImproverName => App.DisplayName(WorkflowType.TextImprover);
    public string DampfName => App.DisplayName(WorkflowType.DampfAblassen);
    public string EmojiName => App.DisplayName(WorkflowType.EmojiText);
    public string ImproverKeys => App.Hotkey(WorkflowType.TextImprover).DisplayLabel;
    public string DampfKeys => App.Hotkey(WorkflowType.DampfAblassen).DisplayLabel;
    public string EmojiKeys => App.Hotkey(WorkflowType.EmojiText).DisplayLabel;
    public string BlitztextKeys => App.Hotkey(WorkflowType.TapTalk).DisplayLabel;

    public static IReadOnlyList<Option> Tones { get; } =
        ModeSettingsDisplay.Tones.Select(t => new Option(t.DisplayName(), t)).ToList();

    public TextTone Tone
    {
        get => App.TextImprovementSettings.Tone;
        set => App.UpdateSettings(s => s.TextImprovement.Tone = value);
    }

    public string ImproverContext
    {
        get => App.TextImprovementSettings.Context;
        set => App.UpdateSettings(s => s.TextImprovement.Context = value ?? "");
    }

    public string ImproverPrompt
    {
        get => App.TextImprovementSettings.SystemPrompt;
        set => App.UpdateSettings(s => s.TextImprovement.SystemPrompt = value ?? "");
    }

    public string ImproverPromptLabel => string.IsNullOrEmpty(App.TextImprovementSettings.SystemPrompt)
        ? "Eigene Anweisung"
        : "Eigene Anweisung (aktiv)";

    public string DampfPrompt
    {
        get => App.DampfAblassenSettings.SystemPrompt;
        set => App.UpdateSettings(s => s.DampfAblassen.SystemPrompt = value ?? "");
    }

    public bool DampfPromptIsCustom => App.DampfAblassenSettings.SystemPrompt != ModePrompts.DampfAblassenDefault;

    public string DampfPromptLabel => DampfPromptIsCustom ? "Anweisung (angepasst)" : "Anweisung (Standard)";

    public static IReadOnlyList<Option> Densities { get; } =
        ModeSettingsDisplay.Densities.Select(d => new Option(d.DisplayName(), d)).ToList();

    public EmojiDensity EmojiDensity
    {
        get => App.EmojiTextSettings.EmojiDensity;
        set => App.UpdateSettings(s => s.EmojiText.EmojiDensity = value);
    }

    public string ImproverSubtitle => WorkflowType.TextImprover.Subtitle();
    public string DampfSubtitle => WorkflowType.DampfAblassen.Subtitle();
    public string EmojiSubtitle => WorkflowType.EmojiText.Subtitle();

    // MARK: Mode per app

    public static IReadOnlyList<Option> SelectableModes(AppState app) =>
        WorkflowTypes.All.Select(t => new Option(app.DisplayName(t), t)).ToList();

    private IReadOnlyList<Option>? _modeOptions;

    public IReadOnlyList<Option> ModeOptions => Stable(ref _modeOptions, SelectableModes(App));

    public string AppRulesIntro =>
        $"Das Blitztext-Kürzel ({BlitztextKeys}) kann je App oder Website automatisch einen anderen Modus nutzen, zum Beispiel Blitztext+ in Outlook oder Gmail. Überall sonst bleibt es wortgenau, und die übrigen Kürzel bleiben, wie sie sind.";

    public IReadOnlyList<RuleRow> Rules =>
        App.Settings.AppModeRules.Select(r => new RuleRow(r, r.AppName, "", r.Mode))
            .Concat(App.Settings.WebsiteModeRules.Select(r => new RuleRow(r, r.Host, "", r.Mode)))
            .ToList();

    public bool HasWebsiteRules => App.Settings.WebsiteModeRules.Count > 0;

    public bool RulesNeedProvider =>
        (App.Settings.AppModeRules.Any(r => r.Mode != WorkflowType.TapTalk)
         || App.Settings.WebsiteModeRules.Any(r => r.Mode != WorkflowType.TapTalk)) && !App.CanRewrite;

    public static readonly (string Name, string Host)[] WebsitePresets =
    [
        ("Gmail", "mail.google.com"),
        ("Outlook im Web", "outlook.office.com"),
        ("Google Docs", "docs.google.com"),
        ("Notion", "notion.so"),
        ("Slack", "app.slack.com"),
        ("Microsoft Teams", "teams.microsoft.com"),
        ("WhatsApp Web", "web.whatsapp.com"),
        ("LinkedIn", "linkedin.com"),
    ];

    public void SetRuleMode(object rule, WorkflowType mode) => App.UpdateSettings(s =>
    {
        switch (rule)
        {
            case AppModeRule appRule:
                var index = s.App.AppModeRules.FindIndex(r => r.ProcessName == appRule.ProcessName);
                if (index >= 0) s.App.AppModeRules[index] = s.App.AppModeRules[index] with { Mode = mode };
                break;
            case WebsiteModeRule siteRule:
                var siteIndex = s.App.WebsiteModeRules.FindIndex(r => r.Host == siteRule.Host);
                if (siteIndex >= 0) s.App.WebsiteModeRules[siteIndex] = s.App.WebsiteModeRules[siteIndex] with { Mode = mode };
                break;
        }
    });

    public void RemoveRule(object rule) => App.UpdateSettings(s =>
    {
        if (rule is AppModeRule appRule) s.App.AppModeRules.RemoveAll(r => r.ProcessName == appRule.ProcessName);
        if (rule is WebsiteModeRule siteRule) s.App.WebsiteModeRules.RemoveAll(r => r.Host == siteRule.Host);
    });

    public void AddAppRule(string processName, string appName)
    {
        if (App.Settings.AppModeRules.Any(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase))) return;
        App.UpdateSettings(s => s.App.AppModeRules.Add(new AppModeRule
        {
            ProcessName = processName.ToLowerInvariant(),
            AppName = appName,
            Mode = WorkflowType.TextImprover,
        }));
    }

    public void AddWebsiteRule(string host)
    {
        if (App.Settings.WebsiteModeRules.Any(r => r.Host == host)) return;
        App.UpdateSettings(s => s.App.WebsiteModeRules.Add(new WebsiteModeRule { Host = host, Mode = WorkflowType.TextImprover }));
    }

    // MARK: Dictionary

    public IReadOnlyList<string> CustomTerms => App.TextImprovementSettings.CustomTerms.ToList();

    public IReadOnlyList<TermReplacement> Replacements => App.TextImprovementSettings.Replacements.ToList();

    public bool CorrectCustomTerms
    {
        get => App.Settings.CorrectCustomTermsEnabled;
        set => App.UpdateSettings(s => s.App.CorrectCustomTermsEnabled = value);
    }

    public bool RemoveFillerWords
    {
        get => App.Settings.RemoveFillerWordsEnabled;
        set => App.UpdateSettings(s => s.App.RemoveFillerWordsEnabled = value);
    }

    public static IReadOnlyList<Option> Languages { get; } =
    [
        new Option("Deutsch", "de"),
        new Option("Englisch", "en"),
        new Option("Andere", "other"),
    ];

    public string Language
    {
        get => App.TranscriptionSettings.Language;
        set => App.UpdateSettings(s => s.Transcription.Language = value ?? "de");
    }

    public void AddTerm(string term)
    {
        var trimmed = term.Trim();
        if (trimmed.Length == 0 || App.TextImprovementSettings.CustomTerms.Contains(trimmed)) return;
        App.UpdateSettings(s => s.TextImprovement.CustomTerms.Add(trimmed));
    }

    public void RemoveTerm(string term) => App.UpdateSettings(s => s.TextImprovement.CustomTerms.Remove(term));

    public void AddReplacement(string spoken, string written)
    {
        var rule = new TermReplacement(spoken.Trim(), written.Trim());
        if (rule.Spoken.Length == 0 || rule.Written.Length == 0) return;
        App.UpdateSettings(s =>
        {
            s.TextImprovement.Replacements.RemoveAll(r => r.Id == rule.Id);
            s.TextImprovement.Replacements.Add(rule);
        });
    }

    public void RemoveReplacement(TermReplacement rule) =>
        App.UpdateSettings(s => s.TextImprovement.Replacements.RemoveAll(r => r.Id == rule.Id));
}
