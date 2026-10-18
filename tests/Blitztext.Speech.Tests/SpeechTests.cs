using System.Net;
using System.Security.Cryptography;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;
using Xunit.Abstractions;

namespace Blitztext.Speech.Tests;

/// <summary>
/// Real transcriptions need a model on disk. Set BLITZTEXT_TEST_MODELS to a folder laid out
/// like the app's model folder (e.g. parakeet-tdt-0.6b-v3/encoder.int8.onnx ...) and
/// BLITZTEXT_TEST_AUDIO to a folder with de.wav and en.wav; without them these tests only log.
/// </summary>
public class TranscriptionTests(ITestOutputHelper output)
{
    private static string? ModelsRoot => Environment.GetEnvironmentVariable("BLITZTEXT_TEST_MODELS");
    private static string? AudioRoot => Environment.GetEnvironmentVariable("BLITZTEXT_TEST_AUDIO");

    [Theory]
    [InlineData("parakeet-tdt-0.6b-v3", "de.wav")]
    [InlineData("parakeet-tdt-0.6b-v3", "en.wav")]
    [InlineData("whisper-small", "de.wav")]
    [InlineData("whisper-small", "en.wav")]
    public async Task TranscribesSpeech(string modelId, string audioFile)
    {
        if (ModelsRoot is null || AudioRoot is null)
        {
            output.WriteLine("BLITZTEXT_TEST_MODELS / BLITZTEXT_TEST_AUDIO not set, skipped.");
            return;
        }
        var store = new ModelStore(ModelsRoot);
        if (!store.IsInstalled(modelId))
        {
            output.WriteLine($"{modelId} not installed in {ModelsRoot}, skipped.");
            return;
        }
        var clip = AudioConversion.ReadWav(Path.Combine(AudioRoot, audioFile));
        using var service = new LocalTranscriptionService(store);
        var started = DateTime.UtcNow;
        var text = await service.TranscribeAsync(clip.Samples, modelId);
        output.WriteLine($"{modelId} / {audioFile} ({clip.Duration:F1} s audio, {(DateTime.UtcNow - started).TotalSeconds:F1} s): {text}");
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(text.Split(' ').Length >= 3, text);

        // A second run uses the loaded model.
        Assert.True(service.IsReady(modelId));
        Assert.Equal(text, await service.TranscribeAsync(clip.Samples, modelId));
    }

    [Fact]
    public async Task MissingModelSaysSo()
    {
        using var service = new LocalTranscriptionService(new ModelStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
        var error = await Assert.ThrowsAsync<LocalTranscriptionException>(() =>
            service.TranscribeAsync(new float[16_000], "parakeet-tdt-0.6b-v3"));
        Assert.Contains("fehlt", error.Message);
    }
}

public class AudioTests
{
    [Fact]
    public void ResamplesKeepingLengthAndPitch()
    {
        const int rate = 48_000;
        var sine = Enumerable.Range(0, rate * 2).Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5f).ToArray();
        var resampled = AudioConversion.Resample(sine, rate, 16_000);
        Assert.InRange(resampled.Length, 31_900, 32_000);

        // Count zero crossings in the middle second: 440 Hz has 880 per second.
        var crossings = 0;
        for (var i = 8_001; i < 24_000; i++)
        {
            if (resampled[i - 1] < 0 != resampled[i] < 0) crossings++;
        }
        Assert.InRange(crossings, 860, 900);
        Assert.InRange(resampled.Skip(8_000).Take(16_000).Max(), 0.45f, 0.55f);
    }

    [Fact]
    public void MixesStereoToMono()
    {
        var stereo = new float[] { 1, 0, 1, 0, 0.5f, 0.5f };
        Assert.Equal(new float[] { 0.5f, 0.5f, 0.5f }, AudioConversion.ToMono16k(stereo, 16_000, 2));
    }

    [Fact]
    public void ChunksCoverEverySampleWithinTheLimit()
    {
        var samples = new float[16_000 * 95];
        var random = new Random(3);
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(random.NextDouble() - 0.5) * (i % 16_000 < 4_000 ? 0.01f : 1f);
        var chunks = AudioChunker.Chunks(samples, 16_000, 30).ToList();
        Assert.Equal(0, chunks[0].Start);
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].Start + chunks[i - 1].Length, chunks[i].Start);
        Assert.Equal(samples.Length, chunks[^1].Start + chunks[^1].Length);
        Assert.All(chunks, c => Assert.True(c.Length <= 16_000 * 30));
        Assert.Equal([(0, samples.Length)], AudioChunker.Chunks(new float[samples.Length], 16_000, 100).ToList());
    }

    [Fact]
    public void DecodesWhatsAppStyleOpus()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitztext-{Guid.NewGuid()}.opus");
        try
        {
            var encoder = OpusCodecFactory.CreateEncoder(48_000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            using (var file = File.Create(path))
            {
                var ogg = new OpusOggWriteStream(encoder, file);
                var pcm = Enumerable.Range(0, 48_000 * 3)
                    .Select(i => (short)(Math.Sin(2 * Math.PI * 300 * i / 48_000.0) * 8000))
                    .ToArray();
                ogg.WriteSamples(pcm, 0, pcm.Length);
                ogg.Finish();
            }
            Assert.True(AudioConversion.IsOggOpus(path));
            var clip = AudioConversion.ReadOggOpus(path);
            Assert.InRange(clip.Duration, 2.9, 3.1);
            Assert.True(clip.Samples.Max() > 0.1f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ModelInstallerTests
{
    private sealed class FileHandler(string path) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) };
            response.Content.Headers.ContentLength = new FileInfo(path).Length;
            return Task.FromResult(response);
        }
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static (LocalModelInfo Model, string Archive) MakeArchiveModel(string folder, bool corrupt = false)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a.onnx"] = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i % 251)).ToArray(),
            ["tokens.txt"] = System.Text.Encoding.UTF8.GetBytes(new string('x', 1_500_000)),
        };
        var archive = Path.Combine(folder, "model.tar.bz2");
        using (var stream = File.Create(archive))
        using (var writer = WriterFactory.OpenWriter(stream, ArchiveType.Tar, new TarWriterOptions(CompressionType.BZip2, true)))
        {
            foreach (var (name, data) in files)
            {
                writer.Write($"model-folder/{name}", new MemoryStream(data), DateTime.UtcNow);
            }
            writer.Write("model-folder/test_wavs/ignored.wav", new MemoryStream(new byte[10]), DateTime.UtcNow);
        }
        var archiveHash = Sha256(File.ReadAllBytes(archive));
        var model = LocalModelCatalog.Parakeet with
        {
            Id = "test-model",
            DownloadUrl = "https://example.invalid/model.tar.bz2",
            DownloadSize = new FileInfo(archive).Length,
            DownloadChecksum = "sha256:" + (corrupt ? new string('0', 64) : archiveHash),
            ArchiveFolder = "model-folder",
            Files = files.Select(f => new ModelFile(f.Key, f.Value.Length, Sha256(f.Value))).ToList(),
        };
        return (model, archive);
    }

    [Fact]
    public async Task InstallsVerifiedArchiveFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"blitztext-models-{Guid.NewGuid()}");
        Directory.CreateDirectory(root);
        try
        {
            var (model, archive) = MakeArchiveModel(root);
            var store = new ModelStore(Path.Combine(root, "store"));
            var handler = new FileHandler(archive);
            var installer = new ModelInstaller(store, new HttpClient(handler));
            var reports = new List<double>();
            await installer.InstallAsync(model, new SyncProgress(reports.Add));

            Assert.True(store.IsInstalled(model));
            Assert.False(File.Exists(store.PathOf(model, "ignored.wav")));
            Assert.Equal(1, reports[^1]);
            Assert.Equal(reports.OrderBy(r => r), reports);
            Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(store.Root, "downloads")).Any());

            // Already installed: no second download.
            await installer.InstallAsync(model);
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsADownloadWithTheWrongChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), $"blitztext-models-{Guid.NewGuid()}");
        Directory.CreateDirectory(root);
        try
        {
            var (model, archive) = MakeArchiveModel(root, corrupt: true);
            var store = new ModelStore(Path.Combine(root, "store"));
            var installer = new ModelInstaller(store, new HttpClient(new FileHandler(archive)));
            var error = await Assert.ThrowsAsync<ModelInstallException>(() => installer.InstallAsync(model));
            Assert.Contains("Prüfsumme", error.Message);
            Assert.False(store.IsInstalled(model));
            Assert.False(Directory.Exists(store.DirectoryFor(model)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The real Parakeet archive, when BLITZTEXT_TEST_PARAKEET_ARCHIVE points at a downloaded copy.</summary>
    [Fact]
    public async Task InstallsTheRealParakeetArchive()
    {
        var archive = Environment.GetEnvironmentVariable("BLITZTEXT_TEST_PARAKEET_ARCHIVE");
        if (archive is null || !File.Exists(archive)) return;
        var root = Path.Combine(Path.GetTempPath(), $"blitztext-models-{Guid.NewGuid()}");
        try
        {
            var store = new ModelStore(root);
            await new ModelInstaller(store, new HttpClient(new FileHandler(archive))).InstallAsync(LocalModelCatalog.Parakeet);
            Assert.True(store.IsInstalled(LocalModelCatalog.Parakeet));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
