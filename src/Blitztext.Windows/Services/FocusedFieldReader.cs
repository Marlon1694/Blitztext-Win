using System.Runtime.InteropServices;
using Blitztext.Core.Text;
using Interop.UIAutomationClient;

namespace Blitztext.Windows.Services;

/// <summary>What Blitztext can see of the text field a dictation goes into.</summary>
public sealed record FocusedField(string TextBefore = "", string SelectedText = "", string TextAfter = "")
{
    /// <summary>The surrounding text for the AI modes, the same shape the iOS keyboard sends.</summary>
    public string? AiContext
    {
        get
        {
            var parts = new[] { TextBefore, SelectedText, TextAfter }
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
    }
}

/// <summary>
/// Reads the focused text field and the browser address through UI Automation. Password
/// fields are never read. Every call gives up quickly, so an app that hangs cannot hold up a
/// dictation.
/// </summary>
public static class FocusedFieldReader
{
    /// <summary>Characters kept on each side of the cursor.</summary>
    public const int BeforeLimit = 2_000;
    public const int AfterLimit = 600;

    private const int UIA_ProcessIdPropertyId = 30002;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;

    /// <summary>Browsers whose tab address "Modus je Website" can read, by executable name.</summary>
    public static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe",
        "zen.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "chromium.exe", "thorium.exe", "dia.exe",
    };

    public static bool IsBrowser(string? processName) => processName is not null && BrowserProcesses.Contains(processName);

    [ThreadStatic] private static CUIAutomation8Class? _automation;

    private static IUIAutomation Automation()
    {
        if (_automation is null)
        {
            var automation = new CUIAutomation8Class
            {
                // Applies to calls of this client. The default waits up to 20 seconds.
                ConnectionTimeout = 300,
                TransactionTimeout = 300,
            };
            _automation = automation;
        }
        return _automation;
    }

    /// <summary>Text around the cursor in the focused field of <paramref name="processId"/>, or null.</summary>
    public static Task<FocusedField?> ReadAsync(uint processId, TimeSpan timeout) =>
        RunWithTimeout(() => Read(processId), timeout);

    /// <summary>Host of the tab in front, e.g. "mail.google.com".</summary>
    public static Task<string?> WebHostAsync(IntPtr window, uint processId, TimeSpan timeout) =>
        RunWithTimeout(() => WebHost(window, processId), timeout);

    private static async Task<T?> RunWithTimeout<T>(Func<T?> work, TimeSpan timeout) where T : class
    {
        // Thread pool threads are MTA, which UI Automation clients should use.
        var task = Task.Run(() =>
        {
            try { return work(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        });
        var finished = await Task.WhenAny(task, Task.Delay(timeout));
        return finished == task ? await task : null;
    }

    private static FocusedField? Read(uint processId)
    {
        var focused = Automation().GetFocusedElement();
        if (focused is null) return null;
        if (Convert.ToUInt32(focused.GetCurrentPropertyValue(UIA_ProcessIdPropertyId)) != processId) return null;
        if (focused.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true) return null;
        if (focused.GetCurrentPattern(UIA_TextPatternId) is not IUIAutomationTextPattern text) return null;

        var selections = text.GetSelection();
        if (selections is null || selections.Length == 0) return null;
        var selection = selections.GetElement(0);

        var before = selection.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, selection, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
        before.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -BeforeLimit);
        var beforeText = before.GetText(-1) ?? "";
        if (beforeText.Length > BeforeLimit) beforeText = beforeText[^BeforeLimit..];

        var selected = selection.GetText(10_000) ?? "";

        var after = selection.Clone();
        after.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selection, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
        after.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, AfterLimit);
        var afterText = after.GetText(AfterLimit) ?? "";

        // Rich editors end their document with a paragraph mark; that is no text after the cursor.
        if (afterText is "\r" or "\n" or "\r\n") afterText = "";
        return new FocusedField(beforeText, selected, afterText);
    }

    private static string? WebHost(IntPtr window, uint processId)
    {
        var automation = Automation();
        var deadline = DateTime.UtcNow.AddMilliseconds(350);

        // The page around the focused element: the usual case while typing in a web app.
        var walker = automation.ControlViewWalker;
        var node = automation.GetFocusedElement();
        if (node is not null && Convert.ToUInt32(node.GetCurrentPropertyValue(UIA_ProcessIdPropertyId)) == processId)
        {
            for (var steps = 0; node is not null && steps < 40 && DateTime.UtcNow < deadline; steps++)
            {
                if (PageHost(node) is { } host) return host;
                node = walker.GetParentElement(node);
            }
        }

        if (window == IntPtr.Zero || DateTime.UtcNow >= deadline) return null;
        var root = automation.ElementFromHandle(window);
        if (root is null) return null;

        // The web content root carries the address as its value in Chromium and Firefox.
        var document = root.FindFirst(TreeScope.TreeScope_Descendants,
            automation.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_DocumentControlTypeId));
        if (document is not null && PageHost(document) is { } documentHost) return documentHost;
        if (DateTime.UtcNow >= deadline) return null;

        // Last resort: the address bar, the first edit field of the browser window.
        var edit = root.FindFirst(TreeScope.TreeScope_Descendants,
            automation.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_EditControlTypeId));
        return edit is null ? null : HostFromValue(edit);
    }

    private static string? PageHost(IUIAutomationElement element)
    {
        if (Convert.ToInt32(element.GetCurrentPropertyValue(UIA_ControlTypePropertyId)) != UIA_DocumentControlTypeId) return null;
        return HostFromValue(element);
    }

    private static string? HostFromValue(IUIAutomationElement element)
    {
        if (element.GetCurrentPattern(UIA_ValuePatternId) is not IUIAutomationValuePattern value) return null;
        var text = value.CurrentValue;
        return string.IsNullOrWhiteSpace(text) ? null : WebsiteHost.HostOfAddress(text);
    }
}
