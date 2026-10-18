using System.Windows;
using System.Windows.Threading;
using Blitztext.Windows.App;
using Blitztext.Windows.Services;
using Blitztext.Windows.UI;

namespace Blitztext.Windows;

public partial class BlitztextApp : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Unobserved task: " + ex.Exception.GetBaseException().Message);
            ex.SetObserved();
        };

        ThemeManager.Apply(this);
        ThemeManager.Watch(this);

        if (args.Length > 0 && args[0] == "--self-test")
        {
            _ = SelfTest.RunAsync(args.Skip(1).ToArray());
            return;
        }

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            SingleInstance.Forward(args);
            Shutdown();
            return;
        }

        Log.Info($"Blitztext {UpdateService.CurrentVersionText} starts");
        var state = new AppState(new SettingsStore(AppPaths.SettingsFile));
        AppHost.Start(state, args);
        _instance.Listen(AppHost.HandleForwardedArguments);
        SessionEnding += (_, _) => AppHost.Quit();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray app must survive a failing click; the log keeps the details.
        Log.Error("UI: " + e.Exception);
        e.Handled = true;
    }
}
