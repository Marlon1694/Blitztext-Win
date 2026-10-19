using System.Runtime.InteropServices;
using System.Text;
using Blitztext.Core.Settings;
using Blitztext.Core.Workflows;
using Blitztext.Windows.Interop;
using static Blitztext.Windows.Interop.NativeMethods;
using static Blitztext.Windows.Services.InputSender;

namespace Blitztext.Windows.Services;

public enum HotkeyEventKind
{
    /// <summary>The shortcut went down.</summary>
    Down,
    /// <summary>The shortcut was released (hold mode stops here).</summary>
    Up,
    /// <summary>A further modifier joined while held: the dictation becomes another mode.</summary>
    Upgrade,
    /// <summary>Another key was pressed with a modifier-only shortcut, e.g. Strg+Win+→ for desktops.</summary>
    Cancel,
    Escape,
}

/// <param name="Press">Counts presses, so a cancel only stops what the same press started.</param>
public sealed record HotkeyEvent(HotkeyEventKind Kind, WorkflowType Type, long Press, WorkflowType? From = null)
{
    /// <summary>When the hook saw the keys (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</summary>
    public long Timestamp { get; init; } = System.Diagnostics.Stopwatch.GetTimestamp();
}

public abstract record CaptureResult
{
    public sealed record Combo(KeyCombo Value) : CaptureResult;
    public sealed record Cancelled : CaptureResult;
    /// <summary>A plain key or a single modifier would collide with normal typing.</summary>
    public sealed record NeedsModifier : CaptureResult;
}

/// <summary>
/// Global shortcuts through a low-level keyboard hook on its own thread, so a busy window
/// never delays typing. Modifier-only shortcuts ("Strg + Win") fire as soon as exactly
/// those modifiers are held; shortcuts with a key ("Strg + Alt + D") swallow that key.
/// Both report press and release, which hold mode needs.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly LowLevelKeyboardProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    // Configuration, replaced as a whole from the UI thread.
    private volatile IReadOnlyDictionary<WorkflowType, KeyCombo> _combos = new Dictionary<WorkflowType, KeyCombo>();
    private volatile bool _suspended;
    private volatile Action<CaptureResult>? _capture;

    // State, touched only on the hook thread.
    private readonly HashSet<int> _heldModifiers = [];
    private WorkflowType? _activeModifierCombo;
    private ModifierKeys _activeModifiers;
    private (WorkflowType Type, int Key)? _activeKeyCombo;
    private ModifierKeys _suppressed;
    private long _press;
    private ModifierKeys _captureMaxModifiers;
    private bool _captureSawKey;

    public event EventHandler<HotkeyEvent>? HotkeyPressed;

    public HotkeyService()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _proc = HookCallback;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Blitztext keyboard hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    /// <summary>The shortcut each mode listens to.</summary>
    public void UpdateHotkeys(IReadOnlyDictionary<WorkflowType, KeyCombo> combos) =>
        _combos = new Dictionary<WorkflowType, KeyCombo>(combos);

    /// <summary>Temporarily disables all shortcuts.</summary>
    public void SetSuspended(bool suspended) => _suspended = suspended;

    /// <summary>
    /// Records the next shortcut instead of acting on it; keys are swallowed meanwhile.
    /// <paramref name="onResult"/> runs on the UI thread; capture ends with a combo or a cancel.
    /// </summary>
    public void BeginCapture(Action<CaptureResult> onResult)
    {
        _captureMaxModifiers = ModifierKeys.None;
        _captureSawKey = false;
        _capture = result => _ui.Post(_ => onResult(result), null);
    }

    public void EndCapture() => _capture = null;

    // MARK: - Hook

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == (UIntPtr)(ulong)Signature.ToInt64())
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        var message = wParam.ToInt32();
        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var vk = (int)data.vkCode;

        // AltGr sends a fake left Ctrl before right Alt; it is no Ctrl press.
        if (vk == VK_LCONTROL && (data.scanCode & 0x200) != 0)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        bool swallow;
        try
        {
            swallow = _capture is { } capture
                ? HandleCapture(vk, isDown, data.scanCode, data.flags, capture)
                : Handle(vk, isDown);
        }
        catch (Exception)
        {
            // Never let an exception escape into the hook chain.
            swallow = false;
        }
        return swallow ? new IntPtr(1) : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool Handle(int vk, bool isDown)
    {
        var modifier = ModifierOf(vk);
        if (modifier != ModifierKeys.None)
        {
            if (isDown) _heldModifiers.Add(vk);
            else _heldModifiers.Remove(vk);
            HealHeldModifiers(vk);
            EvaluateModifiers(CurrentModifiers());
            return false;
        }

        if (isDown)
        {
            if (_activeKeyCombo is { } active && active.Key == vk) return true; // auto-repeat
            if (vk == VK_ESCAPE) Post(new HotkeyEvent(HotkeyEventKind.Escape, WorkflowType.TapTalk, _press));

            var modifiers = CurrentModifiers();
            if (!_suspended && _activeKeyCombo is null && _activeModifierCombo is null)
            {
                foreach (var (type, combo) in _combos)
                {
                    if (combo.IsModifierOnly || combo.VirtualKey != vk || combo.Modifiers != modifiers) continue;
                    _activeKeyCombo = (type, vk);
                    _press++;
                    Post(new HotkeyEvent(HotkeyEventKind.Down, type, _press));
                    return true;
                }
            }

            if (_activeModifierCombo is { } held)
            {
                // Strg+Win+→ switches desktops: that was no dictation.
                _activeModifierCombo = null;
                _suppressed = modifiers;
                Post(new HotkeyEvent(HotkeyEventKind.Cancel, held, _press));
            }
            else if (modifiers != ModifierKeys.None)
            {
                // A shortcut of another app is under way; adding modifiers must not start a dictation.
                _suppressed |= modifiers;
            }
            return false;
        }

        if (_activeKeyCombo is { } released && released.Key == vk)
        {
            _activeKeyCombo = null;
            Post(new HotkeyEvent(HotkeyEventKind.Up, released.Type, _press));
            return true;
        }
        return false;
    }

    private void EvaluateModifiers(ModifierKeys modifiers)
    {
        if (_suppressed != ModifierKeys.None)
        {
            if ((modifiers & _suppressed) == _suppressed && modifiers != ModifierKeys.None && _activeModifierCombo is null) return;
            _suppressed = ModifierKeys.None;
        }

        if (_activeModifierCombo is not { } active)
        {
            if (_suspended || _activeKeyCombo is not null || modifiers == ModifierKeys.None) return;
            var match = FindModifierCombo(modifiers);
            if (match is not { } type) return;
            _activeModifierCombo = type;
            _activeModifiers = modifiers;
            _press++;
            if ((modifiers & (ModifierKeys.Win | ModifierKeys.Alt)) != 0) SendMaskKey();
            Post(new HotkeyEvent(HotkeyEventKind.Down, type, _press));
            return;
        }

        if ((modifiers & _activeModifiers) != _activeModifiers)
        {
            // One of the shortcut's keys went up.
            _activeModifierCombo = null;
            if (modifiers != ModifierKeys.None) _suppressed = modifiers;
            Post(new HotkeyEvent(HotkeyEventKind.Up, active, _press));
            return;
        }

        if (modifiers != _activeModifiers && FindModifierCombo(modifiers) is { } upgrade && upgrade != active)
        {
            _activeModifierCombo = upgrade;
            _activeModifiers = modifiers;
            Post(new HotkeyEvent(HotkeyEventKind.Upgrade, upgrade, _press, active));
        }
    }

    private WorkflowType? FindModifierCombo(ModifierKeys modifiers)
    {
        foreach (var (type, combo) in _combos)
        {
            if (combo.IsModifierOnly && combo.Modifiers == modifiers) return type;
        }
        return null;
    }

    /// <summary>A key-up missed while another desktop had focus (Win+L, UAC) leaves a key marked as held.</summary>
    private void HealHeldModifiers(int currentKey)
    {
        _heldModifiers.RemoveWhere(vk => vk != currentKey && !IsKeyDown(vk));
    }

    private ModifierKeys CurrentModifiers()
    {
        var result = ModifierKeys.None;
        foreach (var vk in _heldModifiers) result |= ModifierOf(vk);
        return result;
    }

    internal static ModifierKeys ModifierOf(int vk) => vk switch
    {
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => ModifierKeys.Ctrl,
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => ModifierKeys.Shift,
        VK_MENU or VK_LMENU or VK_RMENU => ModifierKeys.Alt,
        VK_LWIN or VK_RWIN => ModifierKeys.Win,
        _ => ModifierKeys.None,
    };

    // MARK: - Capture

    private bool HandleCapture(int vk, bool isDown, uint scanCode, uint flags, Action<CaptureResult> capture)
    {
        var modifier = ModifierOf(vk);
        if (modifier != ModifierKeys.None)
        {
            HealHeldModifiers(vk);
            if (isDown && CurrentModifiers() == ModifierKeys.None)
            {
                // A new chord starts.
                _captureSawKey = false;
                _captureMaxModifiers = ModifierKeys.None;
            }
            if (isDown) _heldModifiers.Add(vk);
            else _heldModifiers.Remove(vk);
            var modifiers = CurrentModifiers();
            _captureMaxModifiers |= modifiers;
            if (!isDown && modifiers == ModifierKeys.None && !_captureSawKey && _captureMaxModifiers != ModifierKeys.None)
            {
                var max = _captureMaxModifiers;
                _captureMaxModifiers = ModifierKeys.None;
                if (KeyCombo.ModifierCount(max) >= 2)
                {
                    _capture = null;
                    capture(new CaptureResult.Combo(new KeyCombo(max)));
                }
                else
                {
                    capture(new CaptureResult.NeedsModifier());
                }
            }
            if (!isDown && modifiers == ModifierKeys.None) _captureSawKey = false;
            return true;
        }

        if (!isDown) return true;
        _captureSawKey = true;
        if (vk == VK_ESCAPE && CurrentModifiers() == ModifierKeys.None)
        {
            _capture = null;
            capture(new CaptureResult.Cancelled());
            return true;
        }
        var current = CurrentModifiers();
        var standsAlone = KeyNames.IsStandaloneKey(vk);
        if (!standsAlone && (current & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            capture(new CaptureResult.NeedsModifier());
            return true;
        }
        _capture = null;
        capture(new CaptureResult.Combo(new KeyCombo(current, vk, KeyNames.Label(vk, scanCode, flags))));
        return true;
    }

    private void Post(HotkeyEvent hotkeyEvent) =>
        _ui.Post(_ => HotkeyPressed?.Invoke(this, hotkeyEvent), null);
}

/// <summary>Names of keys for the shortcut badges.</summary>
internal static class KeyNames
{
    private static readonly Dictionary<int, string> Special = new()
    {
        [0x08] = "Rück",
        [0x09] = "Tab",
        [0x0D] = "Eingabe",
        [0x13] = "Pause",
        [0x14] = "Feststell",
        [0x20] = "Leertaste",
        [0x21] = "Bild ↑",
        [0x22] = "Bild ↓",
        [0x23] = "Ende",
        [0x24] = "Pos1",
        [0x25] = "←",
        [0x26] = "↑",
        [0x27] = "→",
        [0x28] = "↓",
        [0x2C] = "Druck",
        [0x2D] = "Einfg",
        [0x2E] = "Entf",
        [0x5D] = "Menü",
        [0x91] = "Rollen",
    };

    /// <summary>Function keys, Pause, Scroll Lock and the menu key may be a shortcut on their own.</summary>
    public static bool IsStandaloneKey(int vk) => vk is >= 0x70 and <= 0x87 or 0x13 or 0x91 or 0x5D;

    public static string Label(int vk, uint scanCode, uint flags)
    {
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return $"Num {vk - 0x60}";
        if (Special.TryGetValue(vk, out var name)) return name;
        var lParam = (int)((scanCode & 0xFF) << 16) | ((flags & LLKHF_EXTENDED) != 0 ? 1 << 24 : 0);
        var builder = new StringBuilder(64);
        if (GetKeyNameText(lParam, builder, builder.Capacity) > 0) return builder.ToString().ToUpperInvariant();
        return $"Taste {vk}";
    }
}
