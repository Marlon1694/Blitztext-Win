namespace Blitztext.Speech;

public enum SpeechEngineKind
{
    Parakeet,
    Whisper,
}

/// <summary>A file the model needs, with its size and SHA-256 when known.</summary>
public sealed record ModelFile(string Name, long Size, string? Sha256 = null);

/// <summary>
/// Relative speed and accuracy (0...1) to compare local models at a glance. Accuracy follows
/// the German FLEURS word error rates the macOS app uses (<c>1 - WER / 25</c>); speed is a
/// rough estimate for a current PC processor, not measured by Blitztext.
/// </summary>
public sealed record LocalModelRating(double Speed, double Accuracy, string SizeLabel, string Languages);

/// <summary>A speech model Blitztext can download and run on this PC.</summary>
public sealed record LocalModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required SpeechEngineKind Engine { get; init; }
    public required LocalModelRating Rating { get; init; }
    public required string PageUrl { get; init; }
    /// <summary>Credit as plain text, e.g. the CC BY 4.0 attribution.</summary>
    public required string Attribution { get; init; }
    /// <summary>Where to download: a .tar.bz2 archive or a single file.</summary>
    public required string DownloadUrl { get; init; }
    public required long DownloadSize { get; init; }
    /// <summary>Checksum of the download: "sha256:…" or "sha1:…".</summary>
    public required string DownloadChecksum { get; init; }
    /// <summary>Files on disk after installation; for an archive, the entries to extract.</summary>
    public required IReadOnlyList<ModelFile> Files { get; init; }
    /// <summary>Folder inside the archive that holds <see cref="Files"/>; null for a single file.</summary>
    public string? ArchiveFolder { get; init; }
}

public static class LocalModelCatalog
{
    /// <summary>
    /// NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0) as int8 ONNX for sherpa-onnx. Same key as the
    /// macOS app, so a settings file moved between them keeps the choice.
    /// </summary>
    public static readonly LocalModelInfo Parakeet = new()
    {
        Id = "parakeet-tdt-0.6b-v3",
        DisplayName = "Parakeet v3",
        Engine = SpeechEngineKind.Parakeet,
        Rating = new LocalModelRating(
            Speed: 0.9,
            Accuracy: 0.77,
            SizeLabel: "~640 MB",
            Languages: "Erkennt die Sprache selbst: Deutsch, Englisch und 23 weitere europäische Sprachen."),
        PageUrl = "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3",
        Attribution = "Parakeet TDT 0.6B v3 von NVIDIA, CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/), als ONNX-Export von sherpa-onnx.",
        DownloadUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
        DownloadSize = 487_170_055,
        DownloadChecksum = "sha256:5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf",
        ArchiveFolder = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
        Files =
        [
            new ModelFile("encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
            new ModelFile("decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
            new ModelFile("joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
            new ModelFile("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        ],
    };

    /// <summary>OpenAI Whisper small (MIT) in the ggml format of whisper.cpp.</summary>
    public static readonly LocalModelInfo WhisperSmall = new()
    {
        Id = "whisper-small",
        DisplayName = "Whisper Small",
        Engine = SpeechEngineKind.Whisper,
        Rating = new LocalModelRating(0.55, 0.59, "466 MB", "Erkennt die Sprache selbst, rund 100 Sprachen."),
        PageUrl = "https://huggingface.co/ggerganov/whisper.cpp",
        Attribution = "OpenAI Whisper (MIT), im ggml-Format von whisper.cpp.",
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        DownloadSize = 0,
        DownloadChecksum = "sha1:55356645c2b361a969dfd0ef2c5a50d530afd8d5",
        Files = [new ModelFile("ggml-small.bin", 0)],
    };

    /// <summary>OpenAI Whisper large-v3-turbo (MIT), 5-bit quantized for whisper.cpp.</summary>
    public static readonly LocalModelInfo WhisperTurbo = new()
    {
        Id = "whisper-large-v3-turbo",
        DisplayName = "Whisper Large v3 Turbo",
        Engine = SpeechEngineKind.Whisper,
        Rating = new LocalModelRating(0.4, 0.73, "547 MB", "Erkennt die Sprache selbst, rund 100 Sprachen."),
        PageUrl = "https://huggingface.co/ggerganov/whisper.cpp",
        Attribution = "OpenAI Whisper large-v3-turbo (MIT), im ggml-Format von whisper.cpp.",
        DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        DownloadSize = 0,
        DownloadChecksum = "sha1:e050f7970618a659205450ad97eb95a18d69c9ee",
        Files = [new ModelFile("ggml-large-v3-turbo-q5_0.bin", 0)],
    };

    public static readonly IReadOnlyList<LocalModelInfo> All = [Parakeet, WhisperSmall, WhisperTurbo];

    public static string RecommendedModelId => Parakeet.Id;

    public static LocalModelInfo? Find(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The catalog entry for a stored name; unknown or empty names get the recommended model.</summary>
    public static LocalModelInfo Resolve(string? id) => Find(id) ?? Parakeet;
}
