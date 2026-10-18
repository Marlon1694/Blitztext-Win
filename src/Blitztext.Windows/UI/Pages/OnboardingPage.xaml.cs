using System.Windows;
using System.Windows.Controls;
using Blitztext.Windows.App;

namespace Blitztext.Windows.UI.Pages;

public partial class OnboardingPage : UserControl
{
    private readonly AppState _app;

    public OnboardingPage(AppState app)
    {
        InitializeComponent();
        _app = app;
    }

    private void OnLater(object sender, RoutedEventArgs e) => _app.Page = PopoverPage.Main;

    private void OnSetup(object sender, RoutedEventArgs e)
    {
        SettingsPage.InitialTab = SettingsTab.Models;
        _app.Page = PopoverPage.Settings;
    }
}
