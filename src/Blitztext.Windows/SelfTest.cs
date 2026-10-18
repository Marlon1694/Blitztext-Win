using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blitztext.Core.AI;
using Blitztext.Core.Settings;
using Blitztext.Core.Workflows;
using Blitztext.Speech;
using Blitztext.Windows.App;
using Blitztext.Windows.Interop;
using Blitztext.Windows.Services;
using Blitztext.Windows.UI;

namespace Blitztext.Windows;

/// <summary>
/// "Blitztext.exe --self-test &lt;folder&gt; [--install-models] [--audio file.wav]" checks a build on
/// a real Windows: every page renders in light and dark (PNGs land in the folder), the
/// clipboard restore, the keyboard hook, the credential store, DPAPI, model download and local
/// transcription. Runs with its own data folder and never touches the user's settings.
/// </summary>
public static class SelfTest
{
    private sealed record Result(string Name, bool Passed, string Detail, bool Required);

    private static readonly List<Result> Results = [];
    private static readonly List<string> LayoutIssues = [];
    private static readonly BindingErrorListener BindingErrors = new();
    private static string _output = ".";

    public static async Task RunAsync(string[] args)
    {
        var code = 1;
        try
        {
            _output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "selftest");
            Directory.CreateDirectory(_output);
            var home = Path.Combine(_output, "home");
            Environment.SetEnvironmentVariable("BLITZTEXT_HOME", home);
            var installModels = args.Contains("--install-models");
            var audioIndex = Array.IndexOf(args, "--audio");
            var audio = audioIndex >= 0 && audioIndex + 1 < args.Length ? args[audioIndex + 1] : null;

            var state = new AppState(new SettingsStore(AppPaths.SettingsFile));
            await Check("settings round trip", true, () => SettingsRoundTrip(state));
            await Check("history encrypted with DPAPI", true, () => HistoryRoundTrip(state));
            await Check("credential manager", true, CredentialRoundTrip);
            await Check("clipboard paste and restore", true, () => ClipboardRestore(state));
            await Check("keyboard hook", true, () => KeyboardHook(state));
            await Check("spell checker", false, SpellChecker);
            await Check("microphones", false, Microphones);
            await Check("render pages", true, () => RenderPages(state));
            await Check("layout", false, () => Report(LayoutIssues, "no cut-off text, nothing wider than the window"));
            await Check("bindings", false, () => Report(BindingErrors.Messages, "no binding errors"));
            await Check("paste into Notepad", false, () => PasteIntoNotepad(state));
            if (installModels)
            {
                foreach (var model in LocalModelCatalog.All.Where(m => m != LocalModelCatalog.WhisperTurbo))
                {
                    await Check($"install {model.Id}", true, () => InstallModel(state, model));
                }
            }
            if (audio is not null)
            {
                await Check("decode audio file", true, () => DecodeAudio(audio));
                foreach (var model in state.Models.InstalledModels())
                {
                    await Check($"transcribe with {model.Id}", true, () => Transcribe(state, model, audio));
                }
            }
            state.Shutdown();
            code = Results.Any(r => r.Required && !r.Passed) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Results.Add(new Result("self-test", false, ex.ToString(), true));
        }
        finally
        {
            WriteReport(code);
            Application.Current.Shutdown(code);
        }
    }

    private static async Task Check(string name, bool required, Func<Task<string>> check)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var detail = await check();
            Results.Add(new Result(name, true, $"{detail} ({watch.ElapsedMilliseconds} ms)", required));
        }
        catch (Exception ex)
        {
            Results.Add(new Result(name, false, ex.GetType().Name + ": " + ex.Message, required));
        }
    }

    private static Task Check(string name, bool required, Func<string> check) => Check(name, required, () => Task.FromResult(check()));

    private static void WriteReport(int code)
    {
        var report = new StringBuilder();
        foreach (var result in Results)
        {
            var mark = result.Passed ? "PASS" : result.Required ? "FAIL" : "WARN";
            report.AppendLine($"{mark}  {result.Name}: {result.Detail}");
        }
        report.AppendLine(code == 0 ? "RESULT: OK" : "RESULT: FAILED");
        File.WriteAllText(Path.Combine(_output, "selftest.log"), report.ToString());
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Report(List<string> issues, string clean)
    {
        var distinct = issues.Distinct().ToList();
        Expect(distinct.Count == 0, $"{distinct.Count} issues:" + Environment.NewLine + string.Join(Environment.NewLine, distinct.Take(40).Select(i => "      " + i)));
        return clean;
    }

    /// <summary>Collects WPF data binding errors, which WPF otherwise only reports to a debugger.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        private readonly StringBuilder _line = new();

        public override void Write(string? message) => _line.Append(message);

        public override void WriteLine(string? message)
        {
            _line.Append(message);
            lock (Messages) Messages.Add(_line.ToString());
            _line.Clear();
        }
    }

    // MARK: - Checks

    private static string SettingsRoundTrip(AppState state)
    {
        state.UpdateSettings(s =>
        {
            s.App.HotkeyMode = HotkeyMode.Toggle;
            s.TextImprovement.CustomTerms.Add("Blackboat");
        });
        state.SetCustomHotkey(new KeyCombo(ModifierKeys.Ctrl | ModifierKeys.Alt, 0x44, "D"), WorkflowType.EmojiText);
        state.AiProvider = new AIProviderConfig(AIProvider.Custom);
        new SettingsStore(AppPaths.SettingsFile).Flush();
        var store = new SettingsStore(AppPaths.SettingsFile);
        // The app's store writes after a short delay; give it that time.
        Thread.Sleep(500);
        var loaded = store.Load();
        Expect(loaded.App.HotkeyMode == HotkeyMode.Toggle, "hotkey mode not saved");
        Expect(loaded.App.Hotkey(WorkflowType.EmojiText).DisplayLabel == "Strg + Alt + D", "own shortcut not saved");
        Expect(loaded.TextImprovement.CustomTerms.Contains("Blackboat"), "custom term not saved");
        Expect(loaded.AiProvider?.Provider == AIProvider.Custom, "provider not saved");
        state.UpdateSettings(s => s.App.HotkeyMode = HotkeyMode.Hold);
        state.SetCustomHotkey(null, WorkflowType.EmojiText);
        state.AiProvider = null;
        return AppPaths.SettingsFile;
    }

    private static string HistoryRoundTrip(AppState state)
    {
        state.History.Add("Selbsttest Verlauf", WorkflowType.TapTalk);
        var bytes = File.ReadAllBytes(AppPaths.HistoryFile);
        Expect(!Encoding.UTF8.GetString(bytes).Contains("Selbsttest", StringComparison.Ordinal), "history is not encrypted");
        var reloaded = new Core.History.TranscriptHistoryStore(AppPaths.HistoryFile, new DpapiProtector(), HistoryRetention.Day);
        Expect(reloaded.Entries.Any(e => e.Text == "Selbsttest Verlauf"), "history not readable after reload");
        return $"{bytes.Length} bytes";
    }

    private static string CredentialRoundTrip()
    {
        var store = new CredentialApiKeyStore();
        store.Save(AIProvider.Mistral, "selftest-1234567890");
        Expect(store.Get(AIProvider.Mistral) == "selftest-1234567890", "key not read back");
        Expect(store.MaskedKey(AIProvider.Mistral) == "sel…7890", "masked key wrong");
        store.Delete(AIProvider.Mistral);
        Expect(store.Get(AIProvider.Mistral) is null, "key not deleted");
        return "saved, read, deleted";
    }

    private static async Task<string> ClipboardRestore(AppState state)
    {
        var service = new ClipboardPasteService(new Core.Paste.ClipboardRestoreTimings
        {
            MinimumHold = 0.2,
            QuietPeriod = 0.2,
            NoReadTimeout = 1,
            MaximumWait = 3,
        });
        service.Copy("vorher kopiert");
        string? seen = null;
        service.Paste("Diktat aus dem Selbsttest", () => seen = service.ReadText());
        Expect(seen == "Diktat aus dem Selbsttest", $"paste target read '{seen}'");
        await Task.Delay(800);
        var restored = service.ReadText();
        Expect(restored == "vorher kopiert", $"clipboard not restored, has '{restored}'");

        // Newer content wins over the restore.
        service.Paste("zweites Diktat", () => seen = service.ReadText());
        Clipboard.SetText("vom Nutzer neu kopiert");
        await Task.Delay(1500);
        Expect(Clipboard.GetText() == "vom Nutzer neu kopiert", "restore overwrote newer content");
        service.Dispose();
        return "transcript offered lazily, previous content restored, newer content kept";
    }

    private static async Task<string> KeyboardHook(AppState state)
    {
        state.Hotkeys.Start();
        Expect(state.Hotkeys.IsRunning, "SetWindowsHookEx failed");
        var events = new List<HotkeyEvent>();
        void OnEvent(object? sender, HotkeyEvent e) => events.Add(e);
        state.Hotkeys.HotkeyPressed += OnEvent;
        try
        {
            // Strg + Win, as a person would press it (without Blitztext's own marker).
            Send(InputSender.VK_LCONTROL, false);
            Send(InputSender.VK_LWIN, false);
            await Task.Delay(150);
            Send(InputSender.VK_LWIN, true);
            Send(InputSender.VK_LCONTROL, true);
            await Task.Delay(300);
        }
        finally
        {
            state.Hotkeys.HotkeyPressed -= OnEvent;
            state.ResetCurrentWorkflow();
        }
        var kinds = string.Join(", ", events.Select(e => $"{e.Kind}:{e.Type}"));
        // Hosted runners may not deliver injected input to hooks; the hook itself is what must work.
        return events.Count == 0 ? "hook installed; injected keys not delivered on this desktop" : $"events {kinds}";
    }

    private static void Send(int key, bool up)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)key,
                    dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0) | (key == InputSender.VK_LWIN ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
                },
            },
        };
        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// The real path end to end: bring Notepad to the front, paste through the delayed clipboard with
    /// Ctrl+V, read the text before the cursor back through UI Automation, and get the clipboard back.
    /// </summary>
    private static async Task<string> PasteIntoNotepad(AppState state)
    {
        var before = Process.GetProcessesByName("notepad").Select(p => p.Id).ToHashSet();
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })?.Dispose();
        try
        {
            TargetApp? target = null;
            for (var i = 0; i < 100 && target is null; i++)
            {
                await Task.Delay(100);
                var started = Process.GetProcessesByName("notepad").FirstOrDefault(p => !before.Contains(p.Id) && p.MainWindowHandle != IntPtr.Zero);
                if (started is not null) target = new TargetApp(started.MainWindowHandle, (uint)started.Id, "notepad.exe", "Editor");
            }
            Expect(target is not null, "Notepad did not open a window");
            target!.Activate();
            await Task.Delay(700);

            state.Clipboard.Copy("vorher kopiert");
            await state.PasteAtCursorAsync("Hallo Welt.", target);
            await Task.Delay(1500);
            var field = await FocusedFieldReader.ReadAsync(target.ProcessId, TimeSpan.FromSeconds(3));
            Expect(field is not null, $"could not read Notepad's text field (foreground pid {TargetApp.ForegroundProcessId()}, Notepad {target.ProcessId})");
            Expect(field!.TextBefore.EndsWith("Hallo Welt.", StringComparison.Ordinal), $"Notepad has '{field.TextBefore}'");
            await Task.Delay(1500);
            var clipboard = state.Clipboard.ReadText();
            Expect(clipboard == "vorher kopiert", $"clipboard has '{clipboard}' after the paste");
            return "pasted at the cursor, read back through UI Automation, clipboard restored";
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("notepad").Where(p => !before.Contains(p.Id)))
            {
                try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static string SpellChecker()
    {
        var checker = DictionaryWordChecker.Shared;
        var known = checker.IsKnownWord("house") || checker.IsKnownWord("Haus");
        var unknown = !checker.IsKnownWord("Clawdblackboatxyz");
        Expect(known && unknown, $"known={known} unknown={unknown}");
        return "real words known, invented ones not";
    }

    private static string Microphones()
    {
        var microphones = AudioRecorder.Microphones();
        return microphones.Count == 0 ? "no microphone on this machine" : string.Join(", ", microphones.Select(m => m.Name));
    }

    private static async Task<string> RenderPages(AppState state)
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var popover = new PopoverWindow(state);
        var pill = new RecordingPillWindow(state);
        var rendered = new List<string>();
        foreach (var dark in new[] { false, true })
        {
            ThemeManager.ApplyForced(Application.Current, dark);
            var theme = dark ? "dark" : "light";
            foreach (var page in Enum.GetValues<PopoverPage>())
            {
                state.Page = page;
                if (page == PopoverPage.Workflow)
                {
                    // Show the error state, which needs no microphone.
                    state.StartWorkflow(WorkflowType.TapTalk);
                }
                popover.Present(atCursor: false);
                await Settle();
                Render(popover, Path.Combine(_output, $"{theme}-{page.ToString().ToLowerInvariant()}.png"));
                Inspect(popover, $"{theme} {page}");
                rendered.Add($"{theme}-{page}");
                if (page == PopoverPage.Settings && popover.Page(page) is UI.Pages.SettingsPage settings)
                {
                    foreach (var tab in Enum.GetValues<UI.Pages.SettingsTab>().Skip(1))
                    {
                        settings.SelectTab(tab);
                        await Settle();
                        Render(popover, Path.Combine(_output, $"{theme}-settings-{tab.ToString().ToLowerInvariant()}.png"));
                        Inspect(popover, $"{theme} settings {tab}");
                    }
                    settings.SelectTab(UI.Pages.SettingsTab.General);
                }
                state.ResetCurrentWorkflow();
            }
            popover.Dismiss();

            foreach (var status in new TrayStatus[] { new TrayStatus.Recording(WorkflowType.TapTalk), new TrayStatus.Processing(WorkflowType.TextImprover) })
            {
                state.Status = status;
                await Task.Delay(500);
                pill.Opacity = 1;
                await Settle();
                Render(pill, Path.Combine(_output, $"{theme}-pill-{status.GetType().Name.ToLowerInvariant()}.png"));
                Inspect(pill, $"{theme} pill {status.GetType().Name}");
            }
            state.Status = TrayStatus.IdleStatus;
        }
        ThemeManager.Apply(Application.Current);
        return $"{rendered.Count} pages";
    }

    private static async Task Settle()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void Render(Window window, string path)
    {
        if (window.Content is not FrameworkElement content) return;
        content.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * 2));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * 2));
        var bitmap = new RenderTargetBitmap(width, height, 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(ThemeManager.IsDark ? Brushes.Black : Brushes.LightGray, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var pixels = new int[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        if (pixels.Distinct().Take(16).Count() < 16) LayoutIssues.Add($"{Path.GetFileName(path)}: looks blank");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Finds text that does not fit and elements that stick out of the window.</summary>
    private static void Inspect(Window window, string name)
    {
        if (window.Content is not FrameworkElement root) return;
        root.UpdateLayout();
        var right = root.ActualWidth + 1;
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            Rect box;
            try
            {
                box = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (box.Right > right || box.Left < -1)
            {
                LayoutIssues.Add($"{name}: {Describe(element)} spans x {box.Left:F0}..{box.Right:F0}, window is {root.ActualWidth:F0} wide");
            }
            if (element is TextBlock text && text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.None && !string.IsNullOrEmpty(text.Text))
            {
                var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                    VisualTreeHelper.GetDpi(text).PixelsPerDip);
                var available = text.ActualWidth - text.Padding.Left - text.Padding.Right;
                if (formatted.WidthIncludingTrailingWhitespace > available + 1)
                {
                    LayoutIssues.Add($"{name}: text cut off ({formatted.WidthIncludingTrailingWhitespace:F0} > {available:F0}): {Describe(text)}");
                }
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string Describe(FrameworkElement element)
    {
        string? label = element switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string content } => content,
            _ => Descendants(element).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
        };
        var id = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : $"{element.GetType().Name} {element.Name}";
        return label is null ? id : $"{id} \"{(label.Length > 60 ? label[..60] + "…" : label)}\"";
    }

    private static async Task<string> InstallModel(AppState state, LocalModelInfo model)
    {
        await state.Installer.InstallAsync(model);
        Expect(state.Models.IsInstalled(model), "not installed after download");
        return state.Models.DirectoryFor(model);
    }

    private static string DecodeAudio(string path)
    {
        var clip = AudioFileDecoder.Decode(path);
        Expect(clip.Duration > 0.5, "audio too short");
        return $"{clip.Duration:F1} s";
    }

    private static async Task<string> Transcribe(AppState state, LocalModelInfo model, string path)
    {
        var clip = AudioFileDecoder.Decode(path);
        var watch = Stopwatch.StartNew();
        var text = await state.Transcription.TranscribeAsync(clip.Samples, model.Id);
        var load = watch.ElapsedMilliseconds;
        watch.Restart();
        await state.Transcription.TranscribeAsync(clip.Samples, model.Id);
        Expect(text.Split(' ').Length >= 3, $"too little text: '{text}'");
        return $"'{text}' (first run {load} ms incl. loading, second run {watch.ElapsedMilliseconds} ms)";
    }
}
