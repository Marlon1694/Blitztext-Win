using System.Runtime.InteropServices;
using Blitztext.Windows.Interop;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.Services;

/// <summary>Synthetic key presses: the paste shortcut and the key that keeps the Start menu closed.</summary>
internal static class InputSender
{
    /// <summary>Marks Blitztext's own key events, so the keyboard hook ignores them.</summary>
    public static readonly IntPtr Signature = new(0x424C5458); // "BLTX"

    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_V = 0x56;
    /// <summary>
    /// An unassigned key. Pressed while Win or Alt is held, Windows no longer treats their
    /// release as "open Start" or "go to the menu bar" (the trick AutoHotkey uses).
    /// </summary>
    public const int VK_MASK = 0xE8;

    private static readonly int[] Modifiers =
        [VK_LWIN, VK_RWIN, VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LMENU, VK_RMENU];

    public static void SendMaskKey() => Send([Key(VK_MASK, false), Key(VK_MASK, true)]);

    public static bool AnyModifierHeld() => Modifiers.Any(IsKeyDown);

    /// <summary>
    /// Waits until the shortcut's keys are up, so the paste is not read as Win+Ctrl+V or
    /// Alt+Ctrl+V. Keys still held after the timeout are released synthetically.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AnyModifierHeld() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }
        var held = Modifiers.Where(IsKeyDown).ToList();
        if (held.Count == 0) return;
        var inputs = new List<INPUT>();
        if (held.Any(k => k is VK_LWIN or VK_RWIN or VK_LMENU or VK_RMENU))
        {
            inputs.Add(Key(VK_MASK, false));
            inputs.Add(Key(VK_MASK, true));
        }
        inputs.AddRange(held.Select(k => Key(k, true)));
        Send(inputs.ToArray());
    }

    /// <summary>Ctrl+V, as if typed.</summary>
    public static void SendPaste() =>
        Send([Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true)]);

    private static INPUT Key(int virtualKey, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (virtualKey is VK_RCONTROL or VK_RMENU or VK_LWIN or VK_RWIN) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = (ushort)MapVirtualKey((uint)virtualKey, 0),
                    dwFlags = flags,
                    dwExtraInfo = Signature,
                },
            },
        };
    }

    private static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
