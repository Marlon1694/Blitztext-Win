using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Blitztext.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Blitztext.Windows.Services;

public sealed record MicrophoneInfo(string Id, string Name);

/// <summary>
/// The microphone, kept ready between dictations. Opening a WASAPI stream (device, audio effects,
/// format) takes a few hundred milliseconds on many PCs, which used to cost the first word. So the
/// stream is opened and initialized while idle and a shortcut only starts it. No audio flows while
/// it waits. Capture runs on its own thread and keeps going briefly after the shortcut is released,
/// so the last word is complete. Audio stays in memory and never touches the disk.
///
/// With <see cref="KeepOpen"/> the stream runs all the time and a dictation also gets the moment
/// before the shortcut, for microphones that need long to wake up (e.g. Bluetooth headsets).
/// </summary>
public sealed class Microphone : IDisposable
{
    private const int BufferMilliseconds = 100;
    private const int KeepOpenPreRollMilliseconds = 400;
    private const uint AccessDenied = 0x80070005;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _commandSignal = new(false);

    // Everything below belongs to the microphone thread.
    private MMDeviceEnumerator? _enumerator;
    private OpenStream? _stream;
    private MicrophoneRecording? _active;
    private byte[] _buffer = new byte[64 * 1024];
    private float[] _ring = [];
    private int _ringWrite;
    private int _ringFilled;
    private bool _keepOpen;
    private string? _preparedFor;
    private bool _disposed;

    public Microphone()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Blitztext microphone" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public static IReadOnlyList<MicrophoneInfo> Devices()
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

    /// <summary>Opens the microphone ahead of the next dictation; no audio flows until a recording starts.</summary>
    public void Prepare(string? deviceId) => Post(() =>
    {
        _preparedFor = deviceId;
        if (EnsureStream(deviceId, out _, out var error) is null && error is not null) Log.Info("Microphone not ready: " + error);
        else if (_keepOpen) StartStream();
    });

    /// <summary>Runs the microphone all the time, so a dictation includes the moment before the shortcut.</summary>
    public bool KeepOpen
    {
        set => Post(() =>
        {
            if (_keepOpen == value) return;
            _keepOpen = value;
            if (value)
            {
                if (EnsureStream(_preparedFor, out _, out _) is not null) StartStream();
            }
            else if (_active is null)
            {
                StopStream();
            }
        });
    }

    /// <summary>
    /// Starts a recording right away on the microphone thread. <paramref name="requestedAt"/> is the
    /// moment of the shortcut (<see cref="Stopwatch.GetTimestamp"/>), used to measure the start delay.
    /// <paramref name="onFailed"/> runs on the microphone thread if the device cannot record.
    /// </summary>
    public MicrophoneRecording Start(string? deviceId, long requestedAt, Action<string> onFailed)
    {
        var recording = new MicrophoneRecording(this, requestedAt, onFailed);
        Post(() => Begin(recording, deviceId));
        return recording;
    }

    internal void Stop(MicrophoneRecording recording, TimeSpan postRoll) => Post(() =>
    {
        if (_active != recording) return;
        recording.StopAt = Stopwatch.GetTimestamp() + (long)(postRoll.TotalSeconds * Stopwatch.Frequency);
    });

    internal void Cancel(MicrophoneRecording recording) => Post(() =>
    {
        if (_active == recording) Finish(recording, keep: false);
    });

    public void Dispose()
    {
        if (_disposed) return;
        Post(() => _disposed = true);
        _thread.Join(TimeSpan.FromSeconds(2));
        _commandSignal.Dispose();
    }

    private void Post(Action command)
    {
        _commands.Enqueue(command);
        try { _commandSignal.Set(); } catch (ObjectDisposedException) { }
    }

    // MARK: - Microphone thread

    private void Run()
    {
        while (!_disposed)
        {
            var frameReady = _stream is { Running: true } running ? running.FrameReady : null;
            var timeout = _active?.StopAt is { } stopAt
                ? (int)Math.Clamp((stopAt - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency, 0, BufferMilliseconds)
                : frameReady is null ? Timeout.Infinite : 3 * BufferMilliseconds;
            if (frameReady is null) _commandSignal.WaitOne(timeout);
            else WaitHandle.WaitAny([_commandSignal, frameReady], timeout);

            while (_commands.TryDequeue(out var command))
            {
                try { command(); }
                catch (Exception ex) { Log.Error($"Microphone: {ex.GetType().Name}: {ex.Message}"); }
            }
            if (_disposed) break;
            if (_stream is { Running: true }) ReadPackets();
            if (_active is { StopAt: { } at } done && Stopwatch.GetTimestamp() >= at) Finish(done, keep: true);
        }
        if (_active is { } active) active.Fail("Blitztext wurde beendet.");
        CloseStream();
        _enumerator?.Dispose();
    }

    private void Begin(MicrophoneRecording recording, string? deviceId)
    {
        // A new shortcut ends the previous recording's last moment early.
        if (_active is { } previous) Finish(previous, keep: true);
        _preparedFor = deviceId;

        var stream = EnsureStream(deviceId, out var opened, out var error);
        if (stream is not null && !StartStream())
        {
            // The device went away while it waited (sleep, unplugged, default changed): open it again.
            CloseStream();
            stream = EnsureStream(deviceId, out opened, out error);
            if (stream is not null && !StartStream()) stream = null;
        }
        if (stream is null)
        {
            recording.Fail(error ?? "Aufnahme konnte nicht gestartet werden.");
            return;
        }
        recording.Begin(stream.SampleRate, opened);
        if (_keepOpen) recording.Append(PreRoll(stream.SampleRate));
        _active = recording;
    }

    private void Finish(MicrophoneRecording recording, bool keep)
    {
        if (keep && _stream is { Running: true }) ReadPackets();
        _active = null;
        if (!_keepOpen) StopStream();
        if (keep) recording.Complete();
        else recording.Discard();
    }

    /// <summary>The open stream for the wanted device; opens it when there is none or the device changed.</summary>
    private OpenStream? EnsureStream(string? deviceId, out bool opened, out string? error)
    {
        opened = false;
        error = null;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            var device = PickDevice(_enumerator, deviceId);
            if (device is null)
            {
                error = "Kein Mikrofon gefunden. Bitte ein Mikrofon anschließen oder in Windows aktivieren.";
                CloseStream();
                return null;
            }
            if (_stream is { } current && current.DeviceId == device.ID)
            {
                device.Dispose();
                return current;
            }
            CloseStream();
            _stream = OpenStream.Open(device, BufferMilliseconds);
            _ring = new float[_stream.SampleRate];
            _ringWrite = _ringFilled = 0;
            opened = true;
            return _stream;
        }
        catch (COMException ex) when ((uint)ex.HResult == AccessDenied)
        {
            error = "Windows erlaubt Blitztext keinen Zugriff aufs Mikrofon. Einstellungen > Datenschutz und Sicherheit > Mikrofon: Desktop-Apps den Zugriff erlauben.";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            error = "Aufnahme konnte nicht gestartet werden: " + ex.Message;
        }
        CloseStream();
        return null;
    }

    private bool StartStream()
    {
        if (_stream is not { } stream) return false;
        if (stream.Running) return true;
        try
        {
            stream.Client.Start();
            stream.Running = true;
            return true;
        }
        catch (COMException ex)
        {
            Log.Info($"Microphone start failed: 0x{ex.HResult:X8}");
            return false;
        }
    }

    private void StopStream()
    {
        if (_stream is not { Running: true } stream) return;
        try
        {
            stream.Client.Stop();
            // Drops what is left, so the next recording starts with fresh audio.
            stream.Client.Reset();
            stream.Running = false;
        }
        catch (COMException)
        {
            CloseStream();
        }
        _ringWrite = _ringFilled = 0;
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private void ReadPackets()
    {
        var stream = _stream!;
        try
        {
            while (stream.Capture.GetNextPacketSize() > 0)
            {
                var data = stream.Capture.GetBuffer(out var frames, out var flags);
                var bytes = frames * stream.Format.BlockAlign;
                float[] mono;
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                {
                    mono = new float[frames];
                }
                else
                {
                    if (_buffer.Length < bytes) _buffer = new byte[bytes * 2];
                    Marshal.Copy(data, _buffer, 0, bytes);
                    mono = ToMono(_buffer.AsSpan(0, bytes), stream.Format);
                }
                stream.Capture.ReleaseBuffer(frames);
                if (_active is { } recording) recording.Append(mono);
                if (_keepOpen) Remember(mono);
            }
        }
        catch (COMException ex)
        {
            // Unplugged or disabled while recording: keep what came so far.
            Log.Info($"Microphone stopped delivering: 0x{ex.HResult:X8}");
            CloseStream();
            if (_active is { } recording)
            {
                _active = null;
                recording.Complete();
            }
        }
    }

    private void Remember(float[] samples)
    {
        foreach (var sample in samples)
        {
            _ring[_ringWrite] = sample;
            _ringWrite = (_ringWrite + 1) % _ring.Length;
        }
        _ringFilled = Math.Min(_ring.Length, _ringFilled + samples.Length);
    }

    private float[] PreRoll(int sampleRate)
    {
        var count = Math.Min(_ringFilled, sampleRate * KeepOpenPreRollMilliseconds / 1000);
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = _ring[(_ringWrite - count + i + _ring.Length) % _ring.Length];
        }
        return result;
    }

    private static MMDevice? PickDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch (COMException)
            {
                // Unplugged: fall back to the Windows default.
            }
        }
        return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : null;
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

    /// <summary>An initialized shared-mode WASAPI capture stream, event driven.</summary>
    private sealed class OpenStream : IDisposable
    {
        public required string DeviceId { get; init; }
        public required MMDevice Device { get; init; }
        public required AudioClient Client { get; init; }
        public required AudioCaptureClient Capture { get; init; }
        public required WaveFormat Format { get; init; }
        public required AutoResetEvent FrameReady { get; init; }
        public bool Running { get; set; }
        public int SampleRate => Format.SampleRate;

        public static OpenStream Open(MMDevice device, int bufferMilliseconds)
        {
            var client = device.AudioClient;
            var frameReady = new AutoResetEvent(false);
            try
            {
                var format = client.MixFormat;
                // Shared event mode: buffer duration in 100 ns units, periodicity 0.
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                    bufferMilliseconds * 10_000L, 0, format, Guid.Empty);
                client.SetEventHandle(frameReady.SafeWaitHandle.DangerousGetHandle());
                return new OpenStream
                {
                    DeviceId = device.ID,
                    Device = device,
                    Client = client,
                    Capture = client.AudioCaptureClient,
                    Format = format,
                    FrameReady = frameReady,
                };
            }
            catch
            {
                client.Dispose();
                frameReady.Dispose();
                device.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (Running) Client.Stop();
            }
            catch (COMException)
            {
            }
            Capture.Dispose();
            Client.Dispose();
            Device.Dispose();
            FrameReady.Dispose();
        }
    }
}

/// <summary>One dictation's audio. The app reads its level while it runs and gets 16 kHz mono at the end.</summary>
public sealed class MicrophoneRecording
{
    private readonly Microphone _microphone;
    private readonly Action<string> _onFailed;
    private readonly long _requestedAt;
    private readonly object _lock = new();
    private readonly TaskCompletionSource<AudioClip> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<float> _samples = new(48_000 * 10);
    private int _sampleRate = 48_000;
    private long _firstAudioAt;
    private float _peak;

    internal MicrophoneRecording(Microphone microphone, long requestedAt, Action<string> onFailed)
    {
        _microphone = microphone;
        _requestedAt = requestedAt;
        _onFailed = onFailed;
    }

    /// <summary>True from the shortcut until it is released (the short tail afterwards does not count).</summary>
    public bool IsRecording { get; private set; } = true;

    /// <summary>Input level 0...1 for the waveforms, about -50 to 0 dBFS.</summary>
    public float AudioLevel { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>How long the shortcut was held, in seconds.</summary>
    public double Duration { get; private set; }

    /// <summary>True when the microphone delivered pure digital silence, as with blocked access.</summary>
    public bool WasSilent => _peak == 0;

    /// <summary>From the shortcut to the first audio from the microphone; null if none came.</summary>
    public TimeSpan? StartDelay => _firstAudioAt == 0 ? null : Stopwatch.GetElapsedTime(_requestedAt, _firstAudioAt);

    /// <summary>True when the device had to be opened for this recording instead of waiting ready.</summary>
    public bool OpenedDevice { get; private set; }

    /// <summary>Ends the recording after <paramref name="postRoll"/> more audio and returns it as 16 kHz mono.</summary>
    public Task<AudioClip> StopAsync(TimeSpan postRoll)
    {
        if (IsRecording)
        {
            IsRecording = false;
            Duration = Stopwatch.GetElapsedTime(_requestedAt).TotalSeconds;
            AudioLevel = 0;
            _microphone.Stop(this, postRoll);
        }
        return _done.Task;
    }

    public void Cancel()
    {
        if (!IsRecording && _done.Task.IsCompleted) return;
        IsRecording = false;
        AudioLevel = 0;
        _microphone.Cancel(this);
        _done.TrySetResult(new AudioClip([]));
    }

    internal long? StopAt { get; set; }

    internal void Begin(int sampleRate, bool openedDevice)
    {
        _sampleRate = sampleRate;
        OpenedDevice = openedDevice;
    }

    internal void Append(float[] mono)
    {
        if (mono.Length == 0) return;
        if (_firstAudioAt == 0) _firstAudioAt = Stopwatch.GetTimestamp();
        double sum = 0;
        float peak = 0;
        foreach (var sample in mono)
        {
            sum += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        _peak = Math.Max(_peak, peak);
        if (IsRecording)
        {
            var rms = Math.Sqrt(sum / mono.Length);
            var decibels = rms > 0 ? 20 * Math.Log10(rms) : -160;
            AudioLevel = (float)Math.Clamp((decibels + 50) / 50, 0, 1);
        }
        lock (_lock) _samples.AddRange(mono);
    }

    internal void Complete()
    {
        IsRecording = false;
        AudioLevel = 0;
        float[] raw;
        lock (_lock)
        {
            raw = _samples.ToArray();
            _samples = [];
        }
        var rate = _sampleRate;
        _ = Task.Run(() => _done.TrySetResult(new AudioClip(AudioConversion.Resample(raw, rate, AudioClip.SampleRate))));
    }

    internal void Discard()
    {
        IsRecording = false;
        AudioLevel = 0;
        lock (_lock) _samples = [];
        _done.TrySetResult(new AudioClip([]));
    }

    internal void Fail(string message)
    {
        IsRecording = false;
        AudioLevel = 0;
        ErrorMessage = message;
        _done.TrySetResult(new AudioClip([]));
        _onFailed(message);
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
