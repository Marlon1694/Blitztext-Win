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
///
/// Bluetooth microphones are not kept ready: an open stream may switch the headset to its hands-free
/// profile, which makes music sound poor. They open per dictation unless KeepOpen is on.
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
    private DeviceChanges? _deviceChanges;
    private OpenStream? _stream;
    private MicrophoneRecording? _active;
    private byte[] _buffer = new byte[64 * 1024];
    private float[] _ring = [];
    private int _ringWrite;
    private int _ringFilled;
    private bool _keepOpen;
    private string? _preparedFor;
    private bool _reprepareAfterRecording;
    private int _reprepareQueued;
    private bool _simulateDeviceLoss;
    private long _lastDeviceFailure;
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

    /// <summary>
    /// Opens the microphone ahead of the next dictation; no audio flows until a recording starts.
    /// Also call it after the PC wakes up: a stream from before sleep is checked and reopened.
    /// </summary>
    public void Prepare(string? deviceId) => Post(() =>
    {
        _preparedFor = deviceId;
        Reprepare();
    });

    private void Reprepare()
    {
        if (_active is not null)
        {
            _reprepareAfterRecording = true;
            return;
        }
        _reprepareAfterRecording = false;
        if (_stream is { } current && !current.IsHealthy) CloseStream();
        if (!_keepOpen && WantedDeviceIsBluetooth())
        {
            // Not even opened while idle, see the class comment.
            CloseStream();
            return;
        }
        var stream = EnsureStream(_preparedFor, out _, out var error);
        if (stream is null)
        {
            if (error is not null) Log.Info("Microphone not ready: " + error);
            return;
        }
        if (_keepOpen) StartStream();
    }

    /// <summary>
    /// Gets ready again after the device failed: Windows' new default, or the same device once it is back.
    /// At most once every 5 s, so a device that keeps failing (with KeepOpen running) cannot spin.
    /// </summary>
    private void ReprepareAfterFailure()
    {
        var now = Stopwatch.GetTimestamp();
        var recently = _lastDeviceFailure != 0 && Stopwatch.GetElapsedTime(_lastDeviceFailure, now) < TimeSpan.FromSeconds(5);
        _lastDeviceFailure = now;
        if (!recently || _reprepareAfterRecording) QueueReprepare();
    }

    /// <summary>Device notifications come in bursts and on other threads; one re-prepare covers them.</summary>
    private void QueueReprepare()
    {
        if (Interlocked.Exchange(ref _reprepareQueued, 1) == 1) return;
        Post(() =>
        {
            Volatile.Write(ref _reprepareQueued, 0);
            Reprepare();
        });
    }

    private bool WantedDeviceIsBluetooth()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            using var device = PickDevice(_enumerator, _preparedFor);
            return device is not null && OpenStream.IsBluetoothDevice(device);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
                if (_stream is { IsBluetooth: true }) CloseStream();
            }
        });
    }

    /// <summary>
    /// Starts a recording right away on the microphone thread. <paramref name="requestedAt"/> is the
    /// moment of the shortcut (<see cref="Stopwatch.GetTimestamp"/>), used to measure the start delay.
    /// <paramref name="onEnded"/> runs on the microphone thread when the recording ends by itself: with an
    /// error message if the device cannot record, with null if it went away mid-dictation (audio kept).
    /// </summary>
    public MicrophoneRecording Start(string? deviceId, long requestedAt, Action<string?> onEnded)
    {
        var recording = new MicrophoneRecording(this, requestedAt, onEnded);
        Post(() => Begin(recording, deviceId));
        return recording;
    }

    internal void Stop(MicrophoneRecording recording, TimeSpan postRoll) => Post(() =>
    {
        if (_active != recording)
        {
            // Already ended (device gone, failed start): hand over what there is.
            recording.Complete();
            return;
        }
        recording.StopAt = Stopwatch.GetTimestamp() + (long)(postRoll.TotalSeconds * Stopwatch.Frequency);
    });

    /// <summary>
    /// For the self-test: the next read fails the way Windows reports a microphone that went away
    /// (AUDCLNT_E_DEVICE_INVALIDATED), so the real error path runs without unplugging anything.
    /// </summary>
    internal void SimulateDeviceLoss() => Post(() => _simulateDeviceLoss = true);

    internal void Cancel(MicrophoneRecording recording) => Post(() =>
    {
        if (_active == recording) Finish(recording, keep: false);
    });

    public void Dispose()
    {
        if (_disposed) return;
        Post(() => _disposed = true);
        // A thread stuck in the driver may still wait on the signal; leave it to the GC then.
        if (_thread.Join(TimeSpan.FromSeconds(2))) _commandSignal.Dispose();
    }

    private void Post(Action command)
    {
        _commands.Enqueue(command);
        try { _commandSignal.Set(); } catch (ObjectDisposedException) { }
    }

    // MARK: - Microphone thread

    private void Run()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _deviceChanges = new DeviceChanges(QueueReprepare);
            _enumerator.RegisterEndpointNotificationCallback(_deviceChanges);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Info($"Microphone notifications unavailable: {ex.GetType().Name}");
        }
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
            try
            {
                if (_stream is { Running: true }) ReadPackets();
                if (_active is { StopAt: { } at } done && Stopwatch.GetTimestamp() >= at) Finish(done, keep: true);
            }
            catch (Exception ex)
            {
                // An exception here would end the whole app; end the recording instead.
                Log.Error($"Microphone: {ex.GetType().Name}: {ex.Message}");
                CloseStream();
                if (_active is { } failed)
                {
                    _active = null;
                    failed.EndedByDevice();
                }
                ReprepareAfterFailure();
            }
        }
        if (_active is { } active) active.Fail("Blitztext wurde beendet.");
        CloseStream();
        try
        {
            if (_deviceChanges is not null) _enumerator?.UnregisterEndpointNotificationCallback(_deviceChanges);
        }
        catch (COMException)
        {
        }
        _enumerator?.Dispose();
    }

    private void Begin(MicrophoneRecording recording, string? deviceId)
    {
        try
        {
            BeginRecording(recording, deviceId);
        }
        catch (Exception ex)
        {
            // Whatever goes wrong, the dictation must end instead of waiting forever.
            Log.Error($"Microphone start failed: {ex.GetType().Name}: {ex.Message}");
            if (_active == recording) _active = null;
            if (!_keepOpen)
            {
                StopStream();
                if (_stream is { IsBluetooth: true }) CloseStream();
            }
            recording.Fail("Aufnahme konnte nicht gestartet werden: " + ex.Message);
        }
    }

    private void BeginRecording(MicrophoneRecording recording, string? deviceId)
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
        if (_keepOpen)
        {
            var preRoll = PreRoll(stream.SampleRate);
            // The pre-roll was captured just before now.
            recording.Append(preRoll, MicrophoneRecording.Now100ns() - preRoll.Length * 10_000_000L / stream.SampleRate);
        }
        _active = recording;
        if (opened) Log.Info($"Microphone opened: {stream.Name}, {stream.Format.SampleRate} Hz, {stream.Format.Channels} ch" + (stream.IsBluetooth ? ", Bluetooth" : ""));
    }

    private void Finish(MicrophoneRecording recording, bool keep)
    {
        if (keep && _stream is { Running: true }) ReadPackets();
        _active = null;
        if (!_keepOpen)
        {
            StopStream();
            // Gives the headset its music quality back.
            if (_stream is { IsBluetooth: true }) CloseStream();
        }
        if (keep) recording.Complete();
        else recording.Discard();
        if (_reprepareAfterRecording) Reprepare();
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
        catch (Exception ex) when (ex is UnauthorizedAccessException || (uint)ex.HResult == AccessDenied)
        {
            // .NET turns E_ACCESSDENIED into UnauthorizedAccessException.
            error = "Windows erlaubt Blitztext keinen Zugriff aufs Mikrofon. Einstellungen > Datenschutz und Sicherheit > Mikrofon: Desktop-Apps den Zugriff erlauben.";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or InvalidCastException)
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
            // Windows wants the event non-signaled when the stream starts.
            stream.FrameReady.Reset();
            stream.Client.Start();
            stream.Running = true;
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info($"Microphone start failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
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
        catch (Exception)
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
            if (_simulateDeviceLoss)
            {
                _simulateDeviceLoss = false;
                throw new COMException("Simulated device loss", unchecked((int)0x88890004));
            }
            while (stream.Capture.GetNextPacketSize() > 0)
            {
                var data = stream.Capture.GetBuffer(out var frames, out var flags, out _, out var capturedAt);
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
                if (_active is { } recording) recording.Append(mono, capturedAt);
                if (_keepOpen) Remember(mono);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // Unplugged or disabled while recording: keep what came so far.
            Log.Info($"Microphone stopped delivering: {ex.GetType().Name} 0x{ex.HResult:X8}");
            CloseStream();
            if (_active is { } recording)
            {
                _active = null;
                recording.EndedByDevice();
            }
            ReprepareAfterFailure();
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
        public required bool IsBluetooth { get; init; }
        public required string Name { get; init; }
        public int SampleRate => Format.SampleRate;

        /// <summary>False when Windows invalidated the stream, e.g. after sleep, unplugging or exclusive use.</summary>
        public bool IsHealthy
        {
            get
            {
                try
                {
                    _ = Client.CurrentPadding;
                    return true;
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return false;
                }
            }
        }

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
                    IsBluetooth = IsBluetoothDevice(device),
                    Name = FriendlyName(device),
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

        /// <summary>PKEY_Device_EnumeratorName: the bus the device hangs on, e.g. BTHENUM or USB.</summary>
        private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

        private static string FriendlyName(MMDevice device)
        {
            try
            {
                return device.FriendlyName;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return "Mikrofon";
            }
        }

        public static bool IsBluetoothDevice(MMDevice device)
        {
            static bool Bluetooth(string? id) => id is not null && id.Contains("BTH", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (device.Properties.Contains(EnumeratorName) && Bluetooth(device.Properties[EnumeratorName].Value as string)) return true;
                // The endpoint itself is a software device; its connector leads to the driver, e.g. \\?\BTHHFENUM#....
                var topology = device.DeviceTopology;
                for (uint i = 0; i < topology.ConnectorCount; i++)
                {
                    var connector = topology.GetConnector(i);
                    if (connector.IsConnected && Bluetooth(connector.ConnectedToDeviceId)) return true;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or NotImplementedException)
            {
            }
            return false;
        }

        public void Dispose()
        {
            try
            {
                if (Running) Client.Stop();
            }
            catch (Exception)
            {
            }
            Capture.Dispose();
            Client.Dispose();
            Device.Dispose();
            FrameReady.Dispose();
        }
    }
}

/// <summary>Device added, removed, disabled or a new Windows default: the microphone prepares again.</summary>
internal sealed class DeviceChanges(Action changed) : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
{
    // Runs on a Windows thread and must return quickly: only queue the work.
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ChangedIfCapture(deviceId);
    public void OnDeviceAdded(string pwstrDeviceId) => ChangedIfCapture(pwstrDeviceId);
    public void OnDeviceRemoved(string deviceId) => ChangedIfCapture(deviceId);

    /// <summary>Capture endpoint IDs start with {0.0.1.00000000}; playback ones ({0.0.0...}) do not matter here.</summary>
    private void ChangedIfCapture(string? deviceId)
    {
        if (deviceId is null || deviceId.StartsWith("{0.0.1.", StringComparison.Ordinal)) changed();
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture && role == Role.Console) changed();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}

/// <summary>One dictation's audio. The app reads its level while it runs and gets 16 kHz mono at the end.</summary>
public sealed class MicrophoneRecording
{
    private readonly Microphone _microphone;
    private readonly Action<string?> _onEnded;
    private readonly long _requestedAt;
    private readonly object _lock = new();
    private readonly TaskCompletionSource<AudioClip> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<float> _samples = new(48_000 * 10);
    private int _sampleRate = 48_000;
    /// <summary>Capture time of the first frame, in 100 ns units of the performance counter.</summary>
    private long _firstFrameAt;
    private float _peak;
    private int _ended;

    internal MicrophoneRecording(Microphone microphone, long requestedAt, Action<string?> onEnded)
    {
        _microphone = microphone;
        _requestedAt = requestedAt;
        _onEnded = onEnded;
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

    /// <summary>
    /// From the shortcut to the first recorded audio, by the microphone's own clock; negative when the
    /// recording reaches back before the shortcut (KeepOpen). Null if no audio came.
    /// </summary>
    public TimeSpan? StartDelay => _firstFrameAt == 0 ? null : TimeSpan.FromTicks(_firstFrameAt - To100ns(_requestedAt));

    internal static long Now100ns() => To100ns(Stopwatch.GetTimestamp());

    private static long To100ns(long timestamp) => (long)(timestamp * (10_000_000.0 / Stopwatch.Frequency));

    /// <summary>True when the device had to be opened for this recording instead of waiting ready.</summary>
    public bool OpenedDevice { get; private set; }

    /// <summary>True when the microphone went away during the recording.</summary>
    public bool DeviceLost { get; private set; }

    /// <summary>Ends the recording after <paramref name="postRoll"/> more audio and returns it as 16 kHz mono.</summary>
    public Task<AudioClip> StopAsync(TimeSpan postRoll)
    {
        if (IsRecording)
        {
            IsRecording = false;
            Duration = Stopwatch.GetElapsedTime(_requestedAt).TotalSeconds;
            AudioLevel = 0;
            _microphone.Stop(this, postRoll);
            // A stuck audio driver must not leave the dictation hanging: take what came so far.
            _ = Task.Delay(postRoll + TimeSpan.FromSeconds(3)).ContinueWith(_ =>
            {
                if (_done.Task.IsCompleted) return;
                Log.Error("Microphone did not finish in time; using the audio so far");
                Complete();
            }, TaskScheduler.Default);
        }
        return _done.Task;
    }

    public void Cancel()
    {
        IsRecording = false;
        AudioLevel = 0;
        if (!TryEnd()) return;
        _microphone.Cancel(this);
        _done.TrySetResult(new AudioClip([]));
    }

    /// <summary>True for the first of complete, discard, fail or cancel; the others then do nothing.</summary>
    private bool TryEnd() => Interlocked.Exchange(ref _ended, 1) == 0;

    internal long? StopAt { get; set; }

    internal void Begin(int sampleRate, bool openedDevice)
    {
        _sampleRate = sampleRate;
        OpenedDevice = openedDevice;
    }

    /// <param name="capturedAt">Capture time of the first sample (100 ns performance counter), 0 if unknown.</param>
    internal void Append(float[] mono, long capturedAt)
    {
        if (mono.Length == 0) return;
        if (_firstFrameAt == 0) _firstFrameAt = capturedAt > 0 ? capturedAt : Now100ns();
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

    /// <summary>Hands over the audio after a stop (the app already set <see cref="IsRecording"/> to false).</summary>
    internal void Complete()
    {
        AudioLevel = 0;
        if (!TryEnd()) return;
        HandOver();
    }

    /// <summary>
    /// The device went away mid-dictation (unplugged, Bluetooth dropped). The audio so far is kept and the
    /// app is told, so it can finish the dictation; <see cref="IsRecording"/> stays true until it stops.
    /// </summary>
    internal void EndedByDevice()
    {
        AudioLevel = 0;
        if (!TryEnd()) return;
        DeviceLost = true;
        HandOver();
        _onEnded(null);
    }

    private void HandOver()
    {
        float[] raw;
        lock (_lock)
        {
            raw = _samples.ToArray();
            _samples = [];
        }
        var rate = _sampleRate;
        _ = Task.Run(() =>
        {
            try
            {
                _done.TrySetResult(new AudioClip(AudioConversion.Resample(raw, rate, AudioClip.SampleRate)));
            }
            catch (Exception ex)
            {
                Log.Error($"Recording conversion failed: {ex.GetType().Name}: {ex.Message}");
                _done.TrySetResult(new AudioClip([]));
            }
        });
    }

    internal void Discard()
    {
        IsRecording = false;
        AudioLevel = 0;
        lock (_lock) _samples = [];
        if (TryEnd()) _done.TrySetResult(new AudioClip([]));
    }

    internal void Fail(string message)
    {
        IsRecording = false;
        AudioLevel = 0;
        if (!TryEnd()) return;
        ErrorMessage = message;
        _done.TrySetResult(new AudioClip([]));
        _onEnded(message);
    }
}

/// <summary>
/// Silences the default sound output while Blitztext records, so a podcast or music does not
/// run on over the dictation, and restores it exactly as it was. Pausing would be nicer but
/// would need to know what is playing.
/// </summary>
public sealed class SystemAudioMuter
{
    /// <summary>Outputs Blitztext muted and has not unmuted yet, e.g. a headset that dropped meanwhile.</summary>
    private readonly HashSet<string> _muted = [];
    private bool _muting;

    public void Mute()
    {
        if (_muting) return;
        RestoreIfNeeded();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;
            // Already silent: nothing to do and nothing to restore later.
            if (volume.Mute) return;
            volume.Mute = true;
            _muted.Add(device.ID);
            _muting = true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
        }
    }

    public void RestoreIfNeeded()
    {
        _muting = false;
        if (_muted.Count == 0) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var id in _muted.ToList())
            {
                try
                {
                    using var device = enumerator.GetDevice(id);
                    // Gone for now (unplugged, Bluetooth off): try again next time.
                    if (device.State != DeviceState.Active) continue;
                    device.AudioEndpointVolume.Mute = false;
                    _muted.Remove(id);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (COMException)
        {
        }
    }
}
