using System.IO.Pipes;
using System.Text;
using System.Windows;
using Blitztext.Windows.App;
using Blitztext.Windows.Services;
using Blitztext.Windows.UI;
using Blitztext.Windows.UI.Pages;
using Microsoft.Win32;

namespace Blitztext.Windows;

/// <summary>Wires the app state to its windows: tray icon, popover, recording pill.</summary>
public static class AppHost
{
    private static int _autoHideSuppression;
    private static DateTime _lastDismissed = DateTime.MinValue;

    public static AppState? State { get; private set; }
    public static PopoverWindow? Popover { get; private set; }
    public static RecordingPillWindow? Pill { get; private set; }
    public static TrayIcon? Tray { get; private set; }
    public static bool IsQuitting { get; private set; }

    public static bool AutoHideSuppressed => _autoHideSuppression > 0;

    /// <summary>While a dialog of Blitztext is open, the popover stays.</summary>
    public static void SuppressAutoHide(bool suppress) =>
        _autoHideSuppression = Math.Max(0, _autoHideSuppression + (suppress ? 1 : -1));

    public static void Start(AppState state, IReadOnlyList<string> args)
    {
        State = state;
        Popover = new PopoverWindow(state);
        Pill = new RecordingPillWindow(state);
        Tray = new TrayIcon(state);

        Popover.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) _lastDismissed = DateTime.UtcNow;
        };
        Tray.Clicked += TogglePopover;
        Tray.SettingsRequested += () => ShowPage(PopoverPage.Settings);
        Tray.HistoryRequested += () => ShowPage(PopoverPage.History);
        Tray.AudioFileRequested += ChooseAudioFile;
        Tray.QuitRequested += Quit;

        state.ShowPopoverRequested += () =>
        {
            if (Popover.IsVisible) return;
            Popover.Present(atCursor: false);
        };
        state.DismissPopoverRequested += () => Popover.Dismiss();
        state.ChooseAudioFileRequested += ChooseAudioFile;

        state.Hotkeys.Start();
        if (!state.Hotkeys.IsRunning)
        {
            Log.Error("Keyboard hook could not be installed");
        }

        StartUpdateChecks(state);

        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)).ToList();
        if (files.Count > 0)
        {
            TranscribeFile(files[0]);
        }
        else if (state.ShouldShowOnboarding)
        {
            state.PreparePopoverPresentation();
            Popover.Present(atCursor: false);
        }
        else if (!args.Contains("--autostart"))
        {
            // Started by hand: say where Blitztext lives now.
            Tray.ShowBalloon("Blitztext läuft", $"{state.Hotkey(Core.Workflows.WorkflowType.TapTalk).DisplayLabel} halten und sprechen. Klick auf das Symbol öffnet Blitztext.");
        }
    }

    public static void TogglePopover()
    {
        if (State is null || Popover is null) return;
        if (Popover.IsVisible)
        {
            Popover.Dismiss();
            return;
        }
        // The click on the tray icon took the focus first and closed the window just now.
        if ((DateTime.UtcNow - _lastDismissed).TotalMilliseconds < 350) return;
        State.PreparePopoverPresentation();
        Popover.Present(atCursor: true);
    }

    public static void ShowPage(PopoverPage page)
    {
        if (State is null || Popover is null) return;
        if (!Popover.IsVisible) State.PreparePopoverPresentation();
        State.Page = page;
        Popover.Present(atCursor: false);
    }

    public static void ChooseAudioFile()
    {
        if (State is null || Popover is null) return;
        SuppressAutoHide(true);
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Audiodatei transkribieren",
                Filter = AudioFileDecoder.DialogFilter,
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            if (dialog.ShowDialog() == true) TranscribeFile(dialog.FileName);
        }
        finally
        {
            SuppressAutoHide(false);
        }
    }

    public static void TranscribeFile(string path)
    {
        if (State is null || Popover is null) return;
        State.AudioFile.Transcribe(path);
        if (!Popover.IsVisible) State.PreparePopoverPresentation();
        State.Page = PopoverPage.AudioFile;
        Popover.Present(atCursor: false);
    }

    /// <summary>Arguments from a second start, e.g. "Öffnen mit" on an audio file.</summary>
    public static void HandleForwardedArguments(IReadOnlyList<string> args)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (file is not null) TranscribeFile(file);
        else ShowPage(PopoverPage.Main);
    }

    private static async void StartUpdateChecks(AppState state)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        while (!IsQuitting)
        {
            if (state.Settings.CheckForUpdates) await state.Updates.CheckAsync(userInitiated: false);
            await Task.Delay(TimeSpan.FromHours(12));
        }
    }

    public static void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        try
        {
            State?.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown: " + ex.Message);
        }
        Tray?.Dispose();
        Popover?.Close();
        Pill?.Close();
        Application.Current.Shutdown();
    }
}

/// <summary>One Blitztext per user: a second start hands its arguments to the first one.</summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Name = $"Blitztext.Windows.{Environment.UserName}";
    private readonly Mutex _mutex;
    private CancellationTokenSource? _server;

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Name, out var createdNew);
        IsFirst = createdNew;
    }

    public static bool Forward(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            client.Connect(2000);
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args.Prepend("blitztext")));
            client.Write(payload, 0, payload.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Listen(Action<IReadOnlyList<string>> onArguments)
    {
        var cancel = new CancellationTokenSource();
        _server = cancel;
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cancel.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var lines = (await reader.ReadToEndAsync(cancel.Token)).Split('\n');
                    if (lines.Length == 0 || lines[0] != "blitztext") continue;
                    var args = lines.Skip(1).Where(l => l.Length > 0).ToList();
                    await dispatcher.InvokeAsync(() => onArguments(args));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                }
            }
        });
    }

    public void Dispose()
    {
        _server?.Cancel();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
