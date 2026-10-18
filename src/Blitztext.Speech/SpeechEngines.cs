using System.Text;
using Blitztext.Core.Text;
using SherpaOnnx;
using Whisper.net;

namespace Blitztext.Speech;

/// <summary>A loaded speech model: 16 kHz mono samples in, text out.</summary>
public interface ISpeechEngine : IDisposable
{
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}

/// <summary>
/// Parakeet TDT 0.6B v3 through sherpa-onnx on the CPU: fast, 25 European languages, detects
/// the language itself. Long recordings are cut at quiet moments into pieces of at most half a
/// minute, the length the model handles best.
/// </summary>
public sealed class ParakeetEngine : ISpeechEngine
{
    private const int SampleRate = 16_000;
    private const int MaxChunkSeconds = 30;
    private readonly OfflineRecognizer _recognizer;
    private readonly object _lock = new();

    public ParakeetEngine(ModelStore store)
    {
        var model = LocalModelCatalog.Parakeet;
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = store.PathOf(model, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = store.PathOf(model, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = store.PathOf(model, "joiner.int8.onnx");
        config.ModelConfig.Tokens = store.PathOf(model, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var parts = new List<string>();
        foreach (var (start, length) in AudioChunker.Chunks(samples, SampleRate, MaxChunkSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = new float[length];
            Array.Copy(samples, start, chunk, 0, length);
            string text;
            lock (_lock)
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(SampleRate, chunk);
                _recognizer.Decode(stream);
                text = stream.Result.Text;
            }
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        }
        return string.Join(' ', parts);
    }, cancellationToken);

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>
/// OpenAI Whisper through whisper.cpp. No fixed language: Whisper follows a given language
/// strictly and turns English speech into German, so it detects the language per dictation.
/// Text Whisper is known to invent on silence is dropped.
/// </summary>
public sealed class WhisperEngine : ISpeechEngine
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperEngine(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
            .Build();
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var text = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken))
            {
                text.Append(segment.Text);
            }
            var result = text.ToString().Trim();
            if (HallucinationFilter.IsInvented(result, HallucinationFilter.VoicedSeconds(samples)))
            {
                return "";
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Cuts long audio at the quietest moment near each limit, so no word is split.</summary>
public static class AudioChunker
{
    public static IEnumerable<(int Start, int Length)> Chunks(float[] samples, int sampleRate, int maxSeconds)
    {
        var max = maxSeconds * sampleRate;
        var start = 0;
        while (samples.Length - start > max)
        {
            // Look for the quietest 100 ms window in the last third of the allowed length.
            var window = sampleRate / 10;
            var searchFrom = start + max * 2 / 3;
            var searchTo = start + max - window;
            var bestCut = start + max;
            var bestEnergy = double.MaxValue;
            for (var position = searchFrom; position <= searchTo; position += window / 2)
            {
                double energy = 0;
                for (var i = position; i < position + window; i++) energy += samples[i] * samples[i];
                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    bestCut = position + window / 2;
                }
            }
            yield return (start, bestCut - start);
            start = bestCut;
        }
        if (samples.Length - start > 0) yield return (start, samples.Length - start);
    }
}
