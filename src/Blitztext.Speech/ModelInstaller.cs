using System.Security.Cryptography;
using SharpCompress.Readers;

namespace Blitztext.Speech;

public sealed class ModelInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where models live and whether one is complete. Each model gets its own folder below
/// <see cref="Root"/>; a download only lands there once its checksum matched.
/// </summary>
public sealed class ModelStore(string root)
{
    public string Root { get; } = root;

    public string DirectoryFor(LocalModelInfo model) => Path.Combine(Root, model.Id);

    public string PathOf(LocalModelInfo model, string fileName) => Path.Combine(DirectoryFor(model), fileName);

    public bool IsInstalled(LocalModelInfo model)
    {
        foreach (var file in model.Files)
        {
            var info = new FileInfo(PathOf(model, file.Name));
            if (!info.Exists) return false;
            if (file.Size > 0 ? info.Length != file.Size : info.Length < 1_000_000) return false;
        }
        return true;
    }

    public bool IsInstalled(string? modelId) => LocalModelCatalog.Find(modelId) is { } model && IsInstalled(model);

    public IReadOnlyList<LocalModelInfo> InstalledModels() => LocalModelCatalog.All.Where(IsInstalled).ToList();

    public void Remove(LocalModelInfo model)
    {
        var directory = DirectoryFor(model);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

/// <summary>Downloads a model, checks its checksum and puts its files in place.</summary>
public sealed class ModelInstaller(ModelStore store, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Blitztext-Windows/1.0");
        return client;
    }

    /// <summary>Progress from 0 to 1. Throws <see cref="ModelInstallException"/> with a message for the user.</summary>
    public async Task InstallAsync(LocalModelInfo model, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (store.IsInstalled(model))
        {
            progress?.Report(1);
            return;
        }

        var downloads = Path.Combine(store.Root, "downloads");
        Directory.CreateDirectory(downloads);
        var work = Path.Combine(downloads, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var downloadPath = Path.Combine(work, "download");
            await DownloadAsync(model, downloadPath, progress, cancellationToken);

            var staging = Path.Combine(work, model.Id);
            Directory.CreateDirectory(staging);
            if (model.ArchiveFolder is not null)
            {
                await Task.Run(() => ExtractArchive(model, downloadPath, staging, cancellationToken), cancellationToken);
                File.Delete(downloadPath);
            }
            else
            {
                File.Move(downloadPath, Path.Combine(staging, model.Files[0].Name));
            }

            var destination = store.DirectoryFor(model);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);

            if (!store.IsInstalled(model))
            {
                throw new ModelInstallException($"Das geladene Modell ist unvollständig: {model.DisplayName}");
            }
            progress?.Report(1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new ModelInstallException($"Download fehlgeschlagen: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ModelInstallException($"Modell konnte nicht gespeichert werden: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModelInstallException($"Kein Schreibzugriff auf den Modellordner: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private async Task DownloadAsync(LocalModelInfo model, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelInstallException($"Download fehlgeschlagen ({(int)response.StatusCode}). Bitte später nochmal versuchen.");
        }
        var total = response.Content.Headers.ContentLength ?? (model.DownloadSize > 0 ? model.DownloadSize : null);

        var (algorithm, expected) = ParseChecksum(model.DownloadChecksum);
        using var hash = IncrementalHash.CreateHash(algorithm);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long received = 0;
            var lastReported = -1.0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total is > 0)
                {
                    // The download is most of the work; extraction gets the last few percent.
                    var share = model.ArchiveFolder is null ? 1.0 : 0.95;
                    var fraction = Math.Min(1, (double)received / total.Value) * share;
                    if (fraction - lastReported >= 0.005)
                    {
                        progress?.Report(fraction);
                        lastReported = fraction;
                    }
                }
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelInstallException(
                "Die Prüfsumme des geladenen Modells stimmt nicht. Die Datei wurde verworfen, bitte nochmal versuchen.");
        }
    }

    /// <summary>Extracts exactly the listed files and checks each one's SHA-256.</summary>
    private static void ExtractArchive(LocalModelInfo model, string archivePath, string staging, CancellationToken cancellationToken)
    {
        var wanted = model.Files.ToDictionary(f => $"{model.ArchiveFolder}/{f.Name}", f => f, StringComparer.Ordinal);
        var found = new HashSet<string>();
        using (var stream = File.OpenRead(archivePath))
        using (var reader = ReaderFactory.OpenReader(stream))
        {
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory || reader.Entry.Key is not { } key) continue;
                var normalized = key.Replace('\\', '/').TrimStart('.', '/');
                if (!wanted.TryGetValue(normalized, out var file)) continue;

                var target = Path.Combine(staging, file.Name);
                using (var output = File.Create(target))
                {
                    reader.WriteEntryTo(output);
                }
                if (file.Sha256 is { } sha && !string.Equals(Sha256Of(target), sha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ModelInstallException($"Die Prüfsumme von {file.Name} stimmt nicht.");
                }
                found.Add(normalized);
            }
        }
        if (found.Count != wanted.Count)
        {
            throw new ModelInstallException($"Das geladene Modell ist unvollständig: {model.DisplayName}");
        }
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static (HashAlgorithmName Algorithm, string Expected) ParseChecksum(string checksum)
    {
        var parts = checksum.Split(':', 2);
        return parts[0].ToLowerInvariant() switch
        {
            "sha1" => (HashAlgorithmName.SHA1, parts[1]),
            "sha256" => (HashAlgorithmName.SHA256, parts[1]),
            _ => throw new ArgumentException($"Unknown checksum: {checksum}"),
        };
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
