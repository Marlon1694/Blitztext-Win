using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Blitztext.Core.AI;
using Blitztext.Core.Settings;
using Blitztext.Core.Text;
using Blitztext.Core.Workflows;
using Blitztext.Windows.App;
using Blitztext.Windows.Services;
using Microsoft.Win32;

namespace Blitztext.Windows.UI.Pages;

public enum SettingsTab
{
    General,
    Models,
    Modes,
    Dictionary,
}

public partial class SettingsPage : UserControl
{
    /// <summary>Tab to open next time; set by onboarding and release notes.</summary>
    public static SettingsTab? InitialTab { get; set; }

    private readonly SettingsViewModel _model;
    private Button? _capturing;

    public SettingsPage(AppState app)
    {
        InitializeComponent();
        _model = new SettingsViewModel(app);
        DataContext = _model;
        Tabs.ItemsSource = new[]
        {
            new Option("Allgemein", SettingsTab.General),
            new Option("Modelle", SettingsTab.Models),
            new Option("Modi", SettingsTab.Modes),
            new Option("Wörterbuch", SettingsTab.Dictionary),
        };
        WhatsNewButton.Content = $"Neuigkeiten in {WhatsNew.Version} anzeigen";
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Opened();
            else EndCapture();
        };
    }

    private AppState App => _model.App;

    /// <summary>Without a speech model nothing works, so the models tab comes first then.</summary>
    private void Opened()
    {
        var tab = InitialTab ?? (App.IsConfigured ? SettingsTab.General : SettingsTab.Models);
        InitialTab = null;
        Tabs.SelectedValue = tab;
        ShowTab(tab);
        _model.ReloadMicrophones();
        _model.Refresh();
    }

    public void SelectTab(SettingsTab tab)
    {
        Tabs.SelectedValue = tab;
        ShowTab(tab);
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedValue is SettingsTab tab) ShowTab(tab);
    }

    private void ShowTab(SettingsTab tab)
    {
        GeneralPane.Visibility = tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
        ModelsPane.Visibility = tab == SettingsTab.Models ? Visibility.Visible : Visibility.Collapsed;
        ModesPane.Visibility = tab == SettingsTab.Modes ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPane.Visibility = tab == SettingsTab.Dictionary ? Visibility.Visible : Visibility.Collapsed;
        Scroll.ScrollToTop();
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.Page = PopoverPage.Main;

    // MARK: Shortcuts

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkflowType type } button) return;
        if (_capturing == button)
        {
            EndCapture();
            return;
        }
        EndCapture();
        _capturing = button;
        button.Content = "Drücken …";
        CaptureHint.Text = "Neue Tastenkombination drücken. Escape bricht ab.";
        CaptureHint.Visibility = Visibility.Visible;
        App.Hotkeys.BeginCapture(result =>
        {
            switch (result)
            {
                case CaptureResult.Combo combo:
                    App.SetCustomHotkey(combo.Value, type);
                    EndCapture();
                    break;
                case CaptureResult.Cancelled:
                    EndCapture();
                    break;
                case CaptureResult.NeedsModifier:
                    CaptureHint.Text = "Bitte mit Strg, Alt oder Win kombinieren, oder zwei Zusatztasten zusammen drücken. F-Tasten gehen auch allein.";
                    break;
            }
        });
    }

    private void EndCapture()
    {
        if (_capturing is null) return;
        App.Hotkeys.EndCapture();
        _capturing = null;
        CaptureHint.Visibility = Visibility.Collapsed;
        _model.Refresh();
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorkflowType type }) App.SetCustomHotkey(null, type);
    }

    // MARK: About

    private void OnGitHub(object sender, RoutedEventArgs e) => Shell.Open(UpdateService.RepositoryUrl);

    private void OnWhatsNew(object sender, RoutedEventArgs e) => App.Page = PopoverPage.WhatsNew;

    private void OnLicenses(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        if (File.Exists(path)) Shell.Open(path);
        else Shell.Open(UpdateService.RepositoryUrl + "/blob/master/THIRD_PARTY_NOTICES.md");
    }

    private void OnLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        Shell.Open(AppPaths.LogsDirectory);
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e) => _ = App.Updates.CheckAsync(userInitiated: true);

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (await App.Updates.InstallAsync()) AppHost.Quit();
    }

    private void OnCleanup(object sender, RoutedEventArgs e)
    {
        AppHost.SuppressAutoHide(true);
        try
        {
            var answer = MessageBox.Show(
                "Einstellungen, Verlauf, API-Keys, Sprachmodelle und den Autostart von diesem PC löschen?\n\nDanach kannst du Blitztext über „Apps“ in den Windows-Einstellungen deinstallieren.",
                "Blitztext entfernen",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
            var error = App.DeleteAllLocalData();
            _model.CleanupText = error ?? "Lokale Daten wurden gelöscht. Jetzt Blitztext beenden und über Einstellungen > Apps deinstallieren.";
            if (error is null) Shell.Open("ms-settings:appsfeatures");
        }
        finally
        {
            AppHost.SuppressAutoHide(false);
        }
    }

    // MARK: Models

    private void OnInstallModel(object sender, RoutedEventArgs e) => App.InstallSelectedLocalModel();

    private void OnRemoveModel(object sender, RoutedEventArgs e)
    {
        AppHost.SuppressAutoHide(true);
        try
        {
            var model = App.SelectedModel;
            var answer = MessageBox.Show($"{model.DisplayName} von diesem PC löschen? Du kannst es jederzeit neu laden.",
                "Modell löschen", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) App.RemoveModel(model);
        }
        finally
        {
            AppHost.SuppressAutoHide(false);
        }
    }

    private void OnModelPage(object sender, RoutedEventArgs e) => Shell.Open(App.SelectedModel.PageUrl);

    // MARK: Provider

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        Ext.SetIsEmpty(KeyBox, KeyBox.Password.Length == 0);
        SaveKeyButton.IsEnabled = KeyBox.Password.Trim().Length > 0;
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        _model.KeyInput = KeyBox.Password;
        _model.SaveKey();
        KeyBox.Clear();
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e) => _model.RemoveKey();

    private void OnKeyHelp(object sender, RoutedEventArgs e)
    {
        if (_model.KeyHelpUrl is { } url) Shell.Open(url);
    }

    private void OnTest(object sender, RoutedEventArgs e) => _model.TestConnection();

    // MARK: Modes

    private void OnResetDampfPrompt(object sender, RoutedEventArgs e) => _model.DampfPrompt = ModePrompts.DampfAblassenDefault;

    private void OnRuleModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only a choice made by the user; bindings filling the box do not count.
        if (sender is not ComboBox { IsLoaded: true } box || !(box.IsDropDownOpen || box.IsKeyboardFocusWithin)) return;
        if (box.Tag is null || box.SelectedValue is not WorkflowType mode) return;
        var current = box.Tag switch
        {
            AppModeRule rule => rule.Mode,
            WebsiteModeRule site => site.Mode,
            _ => mode,
        };
        if (current != mode) _model.SetRuleMode(box.Tag, mode);
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: { } rule }) _model.RemoveRule(rule);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddRuleButton, Placement = PlacementMode.Bottom };
        var existing = App.Settings.AppModeRules.Select(r => r.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (processName, appName) in RunningApps.WithWindows().Where(a => !existing.Contains(a.ProcessName)))
        {
            var item = new MenuItem { Header = appName, ToolTip = processName };
            item.Click += (_, _) => _model.AddAppRule(processName, appName);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var other = new MenuItem { Header = "Andere App …" };
        other.Click += (_, _) => ChooseApp();
        menu.Items.Add(other);
        menu.Items.Add(new Separator());

        var websites = new MenuItem { Header = "Website" };
        var existingHosts = App.Settings.WebsiteModeRules.Select(r => r.Host).ToHashSet();
        foreach (var (name, host) in SettingsViewModel.WebsitePresets.Where(p => !existingHosts.Contains(p.Host)))
        {
            var item = new MenuItem { Header = $"{name} ({host})" };
            item.Click += (_, _) => _model.AddWebsiteRule(host);
            websites.Items.Add(item);
        }
        if (websites.Items.Count > 0) websites.Items.Add(new Separator());
        var otherSite = new MenuItem { Header = "Andere Website …" };
        otherSite.Click += (_, _) => EnterWebsite();
        websites.Items.Add(otherSite);
        menu.Items.Add(websites);
        menu.IsOpen = true;
    }

    private void ChooseApp()
    {
        AppHost.SuppressAutoHide(true);
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "App wählen",
                Filter = "Programme (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            if (dialog.ShowDialog() != true) return;
            var processName = Path.GetFileName(dialog.FileName).ToLowerInvariant();
            _model.AddAppRule(processName, TargetApp.AppDisplayName(dialog.FileName, processName));
        }
        finally
        {
            AppHost.SuppressAutoHide(false);
        }
    }

    private void EnterWebsite()
    {
        AppHost.SuppressAutoHide(true);
        try
        {
            var input = InputDialog.Ask(Window.GetWindow(this), "Website hinzufügen",
                "Adresse der Website, zum Beispiel mail.google.com. Die Regel gilt auch für Unteradressen.", "mail.google.com");
            if (input is not null && WebsiteHost.Normalized(input) is { } host) _model.AddWebsiteRule(host);
        }
        finally
        {
            AppHost.SuppressAutoHide(false);
        }
    }

    // MARK: Dictionary

    private void OnAddTerm(object sender, RoutedEventArgs e)
    {
        _model.AddTerm(NewTerm.Text);
        NewTerm.Clear();
    }

    private void OnTermKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAddTerm(sender, e);
        e.Handled = true;
    }

    private void OnRemoveTerm(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string term }) _model.RemoveTerm(term);
    }

    private void OnAddReplacement(object sender, RoutedEventArgs e)
    {
        if (NewSpoken.Text.Trim().Length == 0 || NewWritten.Text.Trim().Length == 0) return;
        _model.AddReplacement(NewSpoken.Text, NewWritten.Text);
        NewSpoken.Clear();
        NewWritten.Clear();
    }

    private void OnReplacementKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAddReplacement(sender, e);
        e.Handled = true;
    }

    private void OnRemoveReplacement(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TermReplacement rule }) _model.RemoveReplacement(rule);
    }
}
