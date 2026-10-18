using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blitztext.Windows.Services;

/// <summary>
/// Looks for new versions on this project's GitHub releases and installs them with the
/// setup from the release. GitHub sees the request, as with any download.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string Repository = "Marlon1694/Blitztext-Win";
    public static string RepositoryUrl => $"https://github.com/{Repository}";
    public static string ReleasesUrl => $"{RepositoryUrl}/releases";

    private static readonly HttpClient Http = CreateClient();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string? _availableVersion;
    private string? _setupUrl;

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var informational = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var text = informational?.Split('+')[0];
            return string.IsNullOrEmpty(text) ? CurrentVersion.ToString(3) : text;
        }
    }

    public bool UpdateAvailable => AvailableVersion is not null;

    partial void OnAvailableVersionChanged(string? value) => OnPropertyChanged(nameof(UpdateAvailable));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Blitztext-Windows/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (IsChecking) return;
        IsChecking = true;
        if (userInitiated) StatusText = "Suche nach Updates …";
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            var release = JsonNode.Parse(json);
            var tag = release?["tag_name"]?.GetValue<string>() ?? "";
            var latest = ParseVersion(tag);
            _setupUrl = (release?["assets"] as JsonArray)?
                .Select(a => a?["browser_download_url"]?.GetValue<string>())
                .FirstOrDefault(url => url is not null && url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("Setup", StringComparison.OrdinalIgnoreCase));
            if (latest is not null && latest > CurrentVersion)
            {
                AvailableVersion = latest.ToString(3);
                StatusText = $"Version {AvailableVersion} ist verfügbar.";
            }
            else
            {
                AvailableVersion = null;
                StatusText = $"Blitztext ist aktuell (Version {CurrentVersionText}).";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (userInitiated) StatusText = "Update-Prüfung fehlgeschlagen. Bitte später nochmal versuchen.";
            Log.Info("Update check failed: " + ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Downloads the setup and runs it; Blitztext quits so the setup can replace it.</summary>
    public async Task<bool> InstallAsync()
    {
        if (_setupUrl is null)
        {
            OpenReleasesPage();
            return false;
        }
        IsInstalling = true;
        StatusText = "Update wird geladen …";
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"Blitztext-Setup-{AvailableVersion}.exe");
            await using (var source = await Http.GetStreamAsync(_setupUrl))
            await using (var target = File.Create(path))
            {
                await source.CopyToAsync(target);
            }
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            StatusText = "Update konnte nicht geladen werden: " + ex.Message;
            return false;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    public static void OpenReleasesPage() => Shell.Open(ReleasesUrl);

    internal static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
            : null;
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not open {target}: {ex.Message}");
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not reveal {path}: {ex.Message}");
        }
    }
}
