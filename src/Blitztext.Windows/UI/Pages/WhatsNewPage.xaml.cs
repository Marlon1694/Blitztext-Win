using System.Windows;
using System.Windows.Controls;
using Blitztext.Windows.App;

namespace Blitztext.Windows.UI.Pages;

public partial class WhatsNewPage : UserControl
{
    private readonly AppState _app;

    public WhatsNewPage(AppState app)
    {
        InitializeComponent();
        _app = app;
        TitleText.Text = $"Neu in Blitztext {WhatsNew.Version}";
        Items.ItemsSource = WhatsNew.Items;
        IsVisibleChanged += (_, _) => Update();
    }

    private void Update()
    {
        var needsModel = !_app.IsConfigured;
        var needsProvider = !_app.CanRewrite;
        SetupNotice.Visibility = needsModel || needsProvider ? Visibility.Visible : Visibility.Collapsed;
        SetupButton.Visibility = SetupNotice.Visibility;
        if (needsModel)
        {
            SetupTitle.Text = "Noch ein Schritt";
            SetupDetail.Text = "Lade einmal ein lokales Sprachmodell, sonst kann Blitztext nicht transkribieren.";
        }
        else if (needsProvider)
        {
            SetupTitle.Text = "KI-Modi einrichten";
            SetupDetail.Text = "Blitztext+, $%&! und :) brauchen einen Modell-Anbieter: einen eigenen API-Key oder Ollama auf diesem PC.";
        }
    }

    private void OnSetup(object sender, RoutedEventArgs e)
    {
        _app.MarkWhatsNewSeen();
        SettingsPage.InitialTab = SettingsTab.Models;
        _app.Page = PopoverPage.Settings;
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        _app.MarkWhatsNewSeen();
        _app.Page = PopoverPage.Main;
    }
}
