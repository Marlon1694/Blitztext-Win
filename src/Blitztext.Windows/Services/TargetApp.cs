using System.Diagnostics;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.Services;

/// <summary>The window a dictation goes into, captured when it starts.</summary>
public sealed record TargetApp(IntPtr Window, uint ProcessId, string ProcessName, string AppName)
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>The window in front, unless it is Blitztext's own.</summary>
    public static TargetApp? CaptureForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        var root = GetAncestor(window, GA_ROOT);
        if (root != IntPtr.Zero) window = root;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == OwnProcessId) return null;
        var path = ProcessImagePath(processId);
        var processName = path is null ? "" : Path.GetFileName(path).ToLowerInvariant();
        return new TargetApp(window, processId, processName, AppDisplayName(path, processName));
    }

    /// <summary>The product name of an executable, e.g. "Microsoft Outlook" for outlook.exe.</summary>
    public static string AppDisplayName(string? path, string processName)
    {
        if (path is not null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Path.GetFileNameWithoutExtension(processName);
    }

    public static uint ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    public bool IsInFront => ForegroundProcessId() == ProcessId;

    /// <summary>
    /// Brings the window back to the front. Windows only lets the foreground process do this,
    /// so it works while Blitztext's own window is active; the input attach covers the rest.
    /// </summary>
    public void Activate()
    {
        if (!IsWindow(Window)) return;
        if (IsIconic(Window)) ShowWindow(Window, SW_RESTORE);
        if (SetForegroundWindow(Window)) return;

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ownThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ownThread) return;
        if (AttachThreadInput(ownThread, foregroundThread, true))
        {
            try
            {
                BringWindowToTop(Window);
                SetForegroundWindow(Window);
            }
            finally
            {
                AttachThreadInput(ownThread, foregroundThread, false);
            }
        }
    }
}

/// <summary>Apps with a window, for the "Modus je App" picker.</summary>
public static class RunningApps
{
    public static IReadOnlyList<(string ProcessName, string AppName)> WithWindows()
    {
        var own = Environment.ProcessId;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == own || process.MainWindowHandle == IntPtr.Zero) continue;
                var path = ProcessImagePath((uint)process.Id);
                if (path is null) continue;
                var name = Path.GetFileName(path).ToLowerInvariant();
                if (name is "explorer.exe" or "applicationframehost.exe" or "textinputhost.exe" or "searchhost.exe"
                    or "shellexperiencehost.exe" or "startmenuexperiencehost.exe" or "systemsettings.exe") continue;
                result.TryAdd(name, TargetApp.AppDisplayName(path, name));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return result.Select(kv => (kv.Key, kv.Value))
            .OrderBy(a => a.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
