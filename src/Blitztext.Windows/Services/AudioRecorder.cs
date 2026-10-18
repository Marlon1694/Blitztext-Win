using System.Diagnostics;
using System.Runtime.InteropServices;
using Blitztext.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Blitztext.Windows.Services;

public sealed record MicrophoneInfo(string Id, string Name);

/// <summary>
/// Records the microphone through WASAPI in shared mode, keeps the audio in memory (never on
/// disk) and hands out 16 kHz mono samples for the speech model.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private List<float> _samples = [];
    private int _sampleRate = 48_000;
    private TaskCompletionSource? _stopped;
    private readonly Stopwatch _clock = new();
    private float _peak;

    public bool IsRecording { get; private set; }
    public string? ErrorMessage { get; private set; }
    public double LastRecordingDuration { get; private set; }

    /// <summary>Input level 0...1 for the waveforms, about -50 to 0 dBFS.</summary>
    public float AudioLevel { get; private set; }

    public static IReadOnlyList<MicrophoneInfo> Microphones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new MicrophoneInfo(d.ID, d.FriendlyName))
                .ToList();
        }
        catch (COMException)
        {
            return [];
        }
    }

    public void Start(string? deviceId)
    {
        ErrorMessage = null;
        LastRecordingDuration = 0;
        AudioLevel = 0;
        _peak = 0;
        lock (_lock) _samples = new List<float>(16_000 * 30);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = PickDevice(enumerator, deviceId);
            if (_device is null)
            {
                ErrorMessage = "Kein Mikrofon gefunden. Bitte ein Mikrofon anschließen oder in Windows aktivieren.";
                return;
            }
            var capture = new WasapiCapture(_device, true, 50);
            _sampleRate = capture.WaveFormat.SampleRate;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null && IsRecording)
                {
                    ErrorMessage = "Aufnahme fehlgeschlagen: " + args.Exception.Message;
                }
                _stopped?.TrySetResult();
            };
            capture.StartRecording();
            _capture = capture;
            _clock.Restart();
            IsRecording = true;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005)
        {
            ErrorMessage = "Windows erlaubt Blitztext keinen Zugriff aufs Mikrofon. Einstellungen > Datenschutz und Sicherheit > Mikrofon: Desktop-Apps den Zugriff erlauben.";
            Cleanup();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            ErrorMessage = "Aufnahme konnte nicht gestartet werden: " + ex.Message;
            Cleanup();
        }
    }

    /// <summary>Stops and returns the recording as 16 kHz mono.</summary>
    public async Task<AudioClip> StopAsync()
    {
        if (!IsRecording || _capture is null) return new AudioClip([]);
        _clock.Stop();
        LastRecordingDuration = _clock.Elapsed.TotalSeconds;
        IsRecording = false;
        AudioLevel = 0;
        try
        {
            _capture.StopRecording();
            if (_stopped is { } stopped) await Task.WhenAny(stopped.Task, Task.Delay(1_000));
        }
        catch (COMException)
        {
        }
        float[] raw;
        lock (_lock) raw = _samples.ToArray();
        var rate = _sampleRate;
        Cleanup();
        var samples = await Task.Run(() => AudioConversion.Resample(raw, rate, AudioClip.SampleRate));
        return new AudioClip(samples);
    }

    /// <summary>True when the microphone delivered pure digital silence, as with blocked access.</summary>
    public bool LastRecordingWasSilent => _peak == 0;

    public void Cancel()
    {
        if (!IsRecording) return;
        IsRecording = false;
        AudioLevel = 0;
        try { _capture?.StopRecording(); } catch (COMException) { }
        Cleanup();
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        if (sender is not WasapiCapture capture) return;
        var mono = ToMono(args.Buffer.AsSpan(0, args.BytesRecorded), capture.WaveFormat);
        if (mono.Length == 0) return;
        double sum = 0;
        float peak = 0;
        foreach (var sample in mono)
        {
            sum += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        var rms = Math.Sqrt(sum / mono.Length);
        var decibels = rms > 0 ? 20 * Math.Log10(rms) : -160;
        AudioLevel = (float)Math.Clamp((decibels + 50) / 50, 0, 1);
        _peak = Math.Max(_peak, peak);
        lock (_lock) _samples.AddRange(mono);
    }

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Converts the shared-mode mix format (usually 32-bit float, stereo) to mono floats.</summary>
    internal static float[] ToMono(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample == 0) return [];
        var frames = bytes.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * channels + channel) * bytesPerSample;
                sum += ReadSample(bytes.Slice(offset, bytesPerSample), isFloat, bytesPerSample);
            }
            mono[frame] = sum / channels;
        }
        return mono;
    }

    private static float ReadSample(ReadOnlySpan<byte> data, bool isFloat, int bytesPerSample)
    {
        if (isFloat)
        {
            return bytesPerSample == 8 ? (float)BitConverter.ToDouble(data) : BitConverter.ToSingle(data);
        }
        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(data) / 32768f,
            3 => ((data[2] << 24) | (data[1] << 16) | (data[0] << 8)) / 2147483648f,
            4 => BitConverter.ToInt32(data) / 2147483648f,
            _ => (data[0] - 128) / 128f,
        };
    }

    private static MMDevice? PickDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active) return device;
            }
            catch (COMException)
            {
                // Unplugged: fall back to the Windows default.
            }
        }
        try
        {
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void Cleanup()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            _capture.Dispose();
            _capture = null;
        }
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        Cancel();
        Cleanup();
    }
}

/// <summary>
/// Silences the default sound output while Blitztext records, so a podcast or music does not
/// run on over the dictation, and restores it exactly as it was. Pausing would be nicer but
/// would need to know what is playing.
/// </summary>
public sealed class SystemAudioMuter
{
    private string? _mutedDeviceId;

    public void Mute()
    {
        if (_mutedDeviceId is not null) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;
            // Already silent: nothing to do and nothing to restore later.
            if (volume.Mute) return;
            volume.Mute = true;
            _mutedDeviceId = device.ID;
        }
        catch (COMException)
        {
        }
    }

    public void RestoreIfNeeded()
    {
        if (_mutedDeviceId is not { } id) return;
        _mutedDeviceId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            device.AudioEndpointVolume.Mute = false;
        }
        catch (COMException)
        {
        }
    }
}
