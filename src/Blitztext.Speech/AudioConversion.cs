using Concentus;
using Concentus.Oggfile;
using NAudio.Dsp;
using NAudio.Wave;

namespace Blitztext.Speech;

/// <summary>16 kHz mono samples, the input of the local speech models.</summary>
public sealed record AudioClip(float[] Samples)
{
    public const int SampleRate = 16_000;

    public double Duration => (double)Samples.Length / SampleRate;
}

public sealed class AudioConversionException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static AudioConversionException Unreadable(Exception? inner = null) =>
        new("Diese Audiodatei kann Blitztext nicht öffnen.", inner);

    public static AudioConversionException Empty() => new("Die Audiodatei ist leer.");
}

public static class AudioConversion
{
    /// <summary>Mixes interleaved samples down to mono and resamples to 16 kHz.</summary>
    public static float[] ToMono16k(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        channels = Math.Max(1, channels);
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++) sum += interleaved[frame * channels + channel];
            mono[frame] = sum / channels;
        }
        return Resample(mono, sampleRate, AudioClip.SampleRate);
    }

    /// <summary>Band-limited resampling (WDL resampler from NAudio).</summary>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        var resampler = new WdlResampler();
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(fromRate, toRate);

        var expected = (int)((long)input.Length * toRate / fromRate);
        var output = new List<float>(expected + 1024);
        const int block = 8192;
        var outBuffer = new float[(int)((long)block * toRate / fromRate) + 1024];
        var position = 0;
        while (position < input.Length)
        {
            var count = Math.Min(block, input.Length - position);
            var needed = resampler.ResamplePrepare(count, 1, out var inBuffer, out var inOffset);
            Array.Copy(input, position, inBuffer, inOffset, Math.Min(needed, count));
            var produced = resampler.ResampleOut(outBuffer, 0, Math.Min(needed, count), outBuffer.Length, 1);
            for (var i = 0; i < produced; i++) output.Add(outBuffer[i]);
            position += Math.Min(needed, count);
        }
        // Flush the filter tail with silence.
        var tail = resampler.ResamplePrepare(block, 1, out var tailBuffer, out var tailOffset);
        Array.Clear(tailBuffer, tailOffset, tail);
        var flushed = resampler.ResampleOut(outBuffer, 0, tail, outBuffer.Length, 1);
        for (var i = 0; i < flushed && output.Count < expected; i++) output.Add(outBuffer[i]);
        if (output.Count > expected) output.RemoveRange(expected, output.Count - expected);
        return output.ToArray();
    }

    /// <summary>Reads any sample provider to the end and converts it to 16 kHz mono.</summary>
    public static float[] ReadAll(ISampleProvider provider)
    {
        var format = provider.WaveFormat;
        var all = new List<float>(format.SampleRate * format.Channels * 10);
        var buffer = new float[format.SampleRate * format.Channels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read));
        }
        return ToMono16k(all.ToArray(), format.SampleRate, format.Channels);
    }

    /// <summary>WAV files (PCM or float) on any platform.</summary>
    public static AudioClip ReadWav(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var samples = ReadAll(reader.ToSampleProvider());
            if (samples.Length == 0) throw AudioConversionException.Empty();
            return new AudioClip(samples);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            throw AudioConversionException.Unreadable(ex);
        }
    }

    /// <summary>Opus in Ogg, as WhatsApp, Telegram and Signal send voice messages.</summary>
    public static AudioClip ReadOggOpus(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var decoder = OpusCodecFactory.CreateDecoder(48_000, 1);
            var ogg = new OpusOggReadStream(decoder, file);
            var samples = new List<float>(48_000 * 30);
            while (ogg.HasNextPacket)
            {
                var packet = ogg.DecodeNextPacket();
                if (packet is null) continue;
                foreach (var value in packet) samples.Add(value / 32768f);
            }
            if (samples.Count == 0) throw AudioConversionException.Empty();
            return new AudioClip(Resample(samples.ToArray(), 48_000, AudioClip.SampleRate));
        }
        catch (AudioConversionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw AudioConversionException.Unreadable(ex);
        }
    }

    public static bool IsOggOpus(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".opus" or ".oga") return true;
        if (extension != ".ogg") return false;
        // Ogg can also carry Vorbis; Opus streams start with "OpusHead".
        try
        {
            var header = new byte[64];
            using var stream = File.OpenRead(path);
            var read = stream.Read(header, 0, header.Length);
            return System.Text.Encoding.ASCII.GetString(header, 0, read).Contains("OpusHead", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
