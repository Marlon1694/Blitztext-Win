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
using NAudio.CoreAudioApi;
using NAudio.Wave;

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
            if (installModels)
            {
                foreach (var model in LocalModelCatalog.All.Where(m => m != LocalModelCatalog.WhisperTurbo))
                {
                    await Check($"install {model.Id}", true, () => InstallModel(state, model));
                }
            }
            // With a model the workflow page shows its real error state (no microphone on CI).
            await Check("render pages", true, () => RenderPages(state));
            await Check("layout", false, () => Report(LayoutIssues, "no cut-off text, nothing wider than the window"));
            await Check("bindings", false, () => Report(BindingErrors.Messages, "no binding errors"));
            await Check("paste into Notepad", false, () => PasteIntoNotepad(state));
            if (audio is not null)
            {
                // A virtual cable (VB-CABLE) plays the speech into a microphone: the real path from shortcut to text.
                var hasCable = VirtualCable() is not null;
                await Check("dictation end to end", hasCable, () => DictateEndToEnd(state, audio));
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
    private static async Task<(TargetApp Target, HashSet<int> Before)> OpenNotepad()
    {
        var before = Process.GetProcessesByName("notepad").Select(p => p.Id).ToHashSet();
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })?.Dispose();
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            var started = Process.GetProcessesByName("notepad").FirstOrDefault(p => !before.Contains(p.Id) && p.MainWindowHandle != IntPtr.Zero);
            if (started is not null)
            {
                var target = new TargetApp(started.MainWindowHandle, (uint)started.Id, "notepad.exe", "Editor");
                target.Activate();
                await Task.Delay(700);
                return (target, before);
            }
        }
        CloseNotepads(before);
        throw new InvalidOperationException("Notepad did not open a window");
    }

    private static void CloseNotepads(HashSet<int> before)
    {
        foreach (var process in Process.GetProcessesByName("notepad").Where(p => !before.Contains(p.Id)))
        {
            try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string> PasteIntoNotepad(AppState state)
    {
        var (target, before) = await OpenNotepad();
        try
        {

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
            CloseNotepads(before);
        }
    }

    /// <summary>VB-CABLE's playback side and its microphone side, if installed.</summary>
    private static (string SpeakerId, string MicrophoneId)? VirtualCable()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var speaker = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
            var microphone = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
            return speaker is null || microphone is null ? null : (speaker.ID, microphone.ID);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a person does: press Strg+Win, start speaking a moment later, let go right at the end.
    /// The speech plays into a virtual microphone; the text must arrive complete, first and last word
    /// included, in Notepad. Three rounds: the microphone waiting ready, a second dictation right after,
    /// and the "Mikrofon bereithalten" mode.
    /// </summary>
    private static async Task<string> DictateEndToEnd(AppState state, string audioPath)
    {
        var cable = VirtualCable() ?? throw new InvalidOperationException("no virtual microphone (VB-CABLE) on this machine");
        var speech = Trimmed(AudioFileDecoder.Decode(audioPath).Samples);
        var expected = Words(state.PostProcessor.Process((await state.Transcription.TranscribeAsync(speech, LocalModelCatalog.Parakeet.Id)).Trim()));
        Expect(expected.Length >= 4, "reference transcription too short");

        var settings = state.Settings.Clone();
        state.UpdateSettings(s =>
        {
            s.App.SelectedLocalTranscriptionModelName = LocalModelCatalog.Parakeet.Id;
            s.App.MicrophoneDeviceId = cable.MicrophoneId;
            // Muting the sound output would also silence the cable.
            s.App.MuteOutputWhileRecording = false;
            s.App.HotkeyMode = HotkeyMode.Hold;
        });
        if (!state.Hotkeys.IsRunning) state.Hotkeys.Start();
        await Task.Delay(1500);

        var (target, before) = await OpenNotepad();
        var report = new List<string>();
        try
        {
            var text = "";
            foreach (var round in new[] { "ready", "again", "kept open" })
            {
                if (round == "kept open")
                {
                    state.UpdateSettings(s => s.App.KeepMicrophoneOpen = true);
                    await Task.Delay(1500);
                }
                target.Activate();
                await Task.Delay(500);
                var pasted = await DictateOnce(state, target, cable.SpeakerId, speech, text.Length);
                text += pasted;
                var words = Words(pasted);
                report.Add($"{round}: '{pasted.Trim()}' [{LastRecordingLog()}]");
                Expect(words.Length > 0 && words[0] == expected[0], $"{round}: first word missing, got '{pasted.Trim()}', expected to start with '{expected[0]}'");
                Expect(words[^1] == expected[^1], $"{round}: last word incomplete, got '{pasted.Trim()}', expected to end with '{expected[^1]}'");
            }
            Expect(!System.Text.RegularExpressions.Regex.IsMatch(text, @"[.!?]\p{L}"), $"dictations not separated by a space: '{text}'");

            // The microphone disappears mid-dictation (unplugged, Bluetooth gone): the dictation ends with
            // what came so far instead of hanging, and works again once the device is back.
            state.UpdateSettings(s => s.App.KeepMicrophoneOpen = false);
            await Task.Delay(1000);
            target.Activate();
            await Task.Delay(500);
            var partial = await DictateOnce(state, target, cable.SpeakerId, speech, text.Length,
                midway: () => SetCableEnabled(false), midwayAfter: TimeSpan.FromSeconds(2.5));
            text += partial;
            report.Add($"unplugged: '{partial.Trim()}' [{LastRecordingLog()}]");
            Expect(Words(partial) is { Length: > 0 } partWords && partWords[0] == expected[0], $"unplugged: got '{partial.Trim()}'");
            Expect(state.ActiveWorkflow is not { IsRecording: true }, "unplugged: still recording");

            await SetCableEnabled(true);
            for (var i = 0; i < 40 && VirtualCable() is null; i++) await Task.Delay(250);
            await Task.Delay(1500);
            target.Activate();
            await Task.Delay(500);
            var again = await DictateOnce(state, target, cable.SpeakerId, speech, text.Length);
            report.Add($"plugged in again: '{again.Trim()}' [{LastRecordingLog()}]");
            var againWords = Words(again);
            Expect(againWords.Length > 0 && againWords[0] == expected[0] && againWords[^1] == expected[^1], $"plugged in again: got '{again.Trim()}'");
            return string.Join(" | ", report);
        }
        finally
        {
            CloseNotepads(before);
            state.UpdateSettings(s =>
            {
                s.App.MicrophoneDeviceId = settings.MicrophoneDeviceId;
                s.App.MuteOutputWhileRecording = settings.MuteOutputWhileRecording;
                s.App.KeepMicrophoneOpen = settings.KeepMicrophoneOpen;
            });
        }
    }

    /// <summary>One dictation; returns the text it added to Notepad.</summary>
    /// <summary>Switches VB-CABLE off or on, like unplugging a microphone.</summary>
    private static async Task SetCableEnabled(bool enabled)
    {
        var verb = enabled ? "Enable" : "Disable";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"Get-PnpDevice -Class MEDIA | Where-Object FriendlyName -like '*VB-Audio*' | {verb}-PnpDevice -Confirm:$false\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is not null) await process.WaitForExitAsync();
    }

    private static async Task<string> DictateOnce(AppState state, TargetApp target, string speakerId, float[] speech, int lengthBefore,
        Func<Task>? midway = null, TimeSpan midwayAfter = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var speaker = enumerator.GetDevice(speakerId);
        var format = speaker.AudioClient.MixFormat;
        var interleaved = Interleaved(AudioConversion.Resample(speech, AudioClip.SampleRate, format.SampleRate), format.Channels);
        var output = new WasapiOut(speaker, AudioClientShareMode.Shared, true, 20);
        try
        {
            output.Init(new RawSourceWaveStream(new MemoryStream(interleaved), WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)));
            return await PlayAndCollect(state, target, output, speech, lengthBefore, midway, midwayAfter);
        }
        finally
        {
            // The device may be gone (unplug round); disposing must not fail the check.
            try { output.Dispose(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }
    }

    private static async Task<string> PlayAndCollect(AppState state, TargetApp target, WasapiOut output, float[] speech, int lengthBefore,
        Func<Task>? midway, TimeSpan midwayAfter)
    {
        Send(InputSender.VK_LCONTROL, false);
        Send(InputSender.VK_LWIN, false);
        await Task.Delay(150);
        output.Play();
        await Task.Delay(400);
        Expect(state.Status is TrayStatus.Recording, $"tray shows {state.Status} while recording");
        var rest = (int)(speech.Length * 1000L / AudioClip.SampleRate) - 60 - 400;
        if (midway is not null)
        {
            var before = Math.Max(0, (int)midwayAfter.TotalMilliseconds - 400);
            await Task.Delay(before);
            await midway();
            rest = Math.Max(0, rest - before);
        }
        // Let go a moment before the last word has fully sounded, as people do.
        await Task.Delay(rest);
        Send(InputSender.VK_LWIN, true);
        Send(InputSender.VK_LCONTROL, true);

        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(100);
            var field = await FocusedFieldReader.ReadAsync(target.ProcessId, TimeSpan.FromSeconds(2));
            if (field is not null && field.TextBefore.Length > lengthBefore && state.ActiveWorkflow is not { Phase: WorkflowPhase.Running })
            {
                await Task.Delay(300);
                field = await FocusedFieldReader.ReadAsync(target.ProcessId, TimeSpan.FromSeconds(2)) ?? field;
                return field.TextBefore[lengthBefore..];
            }
            if (state.ActiveWorkflow?.Phase is WorkflowPhase.Error error) throw new InvalidOperationException("dictation failed: " + error.Message);
            if (state.Status is TrayStatus.Failure)
            {
                var line = File.ReadLines(Path.Combine(AppPaths.LogsDirectory, "blitztext.log")).LastOrDefault(l => l.Contains("ended with an error", StringComparison.Ordinal));
                throw new InvalidOperationException("dictation failed: " + (line ?? "no log"));
            }
        }
        throw new TimeoutException($"nothing arrived in Notepad (status {state.Status})");
    }

    /// <summary>The app's own note about the last recording, e.g. how fast the microphone answered.</summary>
    private static string LastRecordingLog()
    {
        var line = File.ReadLines(Path.Combine(AppPaths.LogsDirectory, "blitztext.log")).LastOrDefault(l => l.Contains(" Recorded ", StringComparison.Ordinal));
        return line is null ? "no log" : line[(line.IndexOf(" Recorded ", StringComparison.Ordinal) + 1)..];
    }

    private static string[] Words(string text) =>
        text.ToLowerInvariant().Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToArray();

    /// <summary>The speech without silence before and after, so it starts with the first word.</summary>
    private static float[] Trimmed(float[] samples)
    {
        const int frame = 160;
        int first = -1, last = -1;
        for (var start = 0; start + frame <= samples.Length; start += frame)
        {
            double sum = 0;
            for (var i = start; i < start + frame; i++) sum += samples[i] * samples[i];
            if (10 * Math.Log10(sum / frame + 1e-12) <= -40) continue;
            if (first < 0) first = start;
            last = start + frame;
        }
        return first < 0 ? samples : samples[first..last];
    }

    private static byte[] Interleaved(float[] mono, int channels)
    {
        var bytes = new byte[mono.Length * channels * sizeof(float)];
        var span = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (var i = 0; i < mono.Length; i++)
        {
            for (var channel = 0; channel < channels; channel++) span[i * channels + channel] = mono[i];
        }
        return bytes;
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
        var microphones = Microphone.Devices();
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
