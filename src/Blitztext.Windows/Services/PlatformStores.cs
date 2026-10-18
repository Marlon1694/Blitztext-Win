using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Blitztext.Core.AI;
using Blitztext.Core.History;
using Blitztext.Core.Settings;
using Microsoft.Win32;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.Services;

/// <summary>Where Blitztext keeps its files.</summary>
public static class AppPaths
{
    /// <summary>Set by the self-test, so it never touches the user's real files.</summary>
    private static string? Override => Environment.GetEnvironmentVariable("BLITZTEXT_HOME") is { Length: > 0 } home ? home : null;

    public static string RoamingDirectory => Override is { } home
        ? Path.Combine(home, "Roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blitztext");

    public static string LocalDirectory => Override is { } home
        ? Path.Combine(home, "Local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blitztext");

    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");

    /// <summary>Local, not roaming: the history never leaves this PC.</summary>
    public static string HistoryFile => Path.Combine(LocalDirectory, "history.dat");

    public static string ModelsDirectory => Path.Combine(LocalDirectory, "models");

    public static string LogsDirectory => Path.Combine(LocalDirectory, "logs");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Blitztext.exe");
}

/// <summary>API keys in the Windows Credential Manager, one generic credential per provider.</summary>
public sealed class CredentialApiKeyStore : IApiKeyStore
{
    private static string Target(AIProvider provider) => $"Blitztext/ai-provider/{provider.Key()}";

    public string? Get(AIProvider provider)
    {
        if (!CredRead(Target(provider), CRED_TYPE_GENERIC, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            var key = Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool Save(AIProvider provider, string key)
    {
        var trimmed = key.Trim();
        Delete(provider);
        if (trimmed.Length == 0) return true;
        var target = Marshal.StringToHGlobalUni(Target(provider));
        var user = Marshal.StringToHGlobalUni(provider.Key());
        var blob = Marshal.StringToHGlobalUni(trimmed);
        try
        {
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(trimmed.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    public void Delete(AIProvider provider) => CredDelete(Target(provider), CRED_TYPE_GENERIC, 0);
}

/// <summary>Encrypts the history for the signed-in Windows user (DPAPI); other accounts cannot read it.</summary>
public sealed class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Blitztext.History.v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[]? Unprotect(byte[] data)
    {
        try { return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>Reads and writes settings.json; writes are coalesced and atomic.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Timer? _timer;
    private string? _pendingJson;

    public SettingsStore(string path) => _path = path;

    public SettingsContainer Load()
    {
        try
        {
            return File.Exists(_path) ? SettingsSerializer.Deserialize(File.ReadAllText(_path)) : new SettingsContainer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsContainer();
        }
    }

    public void Save(SettingsContainer container)
    {
        var json = SettingsSerializer.Serialize(container);
        lock (_lock)
        {
            _pendingJson = json;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(300, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        string? json;
        lock (_lock)
        {
            json = _pendingJson;
            _pendingJson = null;
        }
        if (json is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings: " + ex.Message);
        }
    }
}

/// <summary>Start with Windows through the Run key of the current user.</summary>
public static class LaunchAtLoginService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Blitztext";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            // Task Manager can switch an entry off without removing it.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] state || state.Length == 0 || (state[0] & 1) == 0;
        }
    }

    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                run.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\" --autostart");
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return "Autostart konnte nicht geändert werden: " + ex.Message;
        }
    }
}

/// <summary>A small log for troubleshooting. Never contains dictated text.</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                var path = Path.Combine(AppPaths.LogsDirectory, "blitztext.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
