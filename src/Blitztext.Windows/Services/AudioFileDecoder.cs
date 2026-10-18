using System.Runtime.InteropServices;
using Blitztext.Speech;
using NAudio.Wave;

namespace Blitztext.Windows.Services;

/// <summary>
/// Turns any audio file Windows can read (m4a from Voice Memos, mp3, wav, wma, flac) and Opus
/// in Ogg from WhatsApp and other messengers into the 16 kHz mono samples the models expect.
/// </summary>
public static class AudioFileDecoder
{
    public static readonly string[] Extensions =
        [".m4a", ".mp3", ".wav", ".aac", ".wma", ".flac", ".ogg", ".opus", ".oga", ".mp4", ".m4v", ".mov", ".webm", ".amr", ".3gp", ".aiff", ".aif", ".caf"];

    public static string DialogFilter =>
        "Audiodateien|" + string.Join(';', Extensions.Select(e => "*" + e)) + "|Alle Dateien|*.*";

    public static bool IsAudioFile(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static AudioClip Decode(string path)
    {
        if (!File.Exists(path)) throw AudioConversionException.Unreadable();
        if (AudioConversion.IsOggOpus(path)) return AudioConversion.ReadOggOpus(path);
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try { return AudioConversion.ReadWav(path); }
            catch (AudioConversionException) { /* compressed WAV: Media Foundation below */ }
        }
        try
        {
            using var reader = new MediaFoundationReader(path);
            var samples = AudioConversion.ReadAll(reader.ToSampleProvider());
            if (samples.Length == 0) throw AudioConversionException.Empty();
            return new AudioClip(samples);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            throw AudioConversionException.Unreadable(ex);
        }
    }
}
