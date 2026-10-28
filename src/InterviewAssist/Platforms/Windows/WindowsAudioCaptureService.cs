using System.Diagnostics;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Audio;
using InterviewAssist.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;

namespace InterviewAssist.Native;

/// <summary>
/// WASAPI capture of either an output device (loopback: what the interviewer says through Zoom/Teams/Meet) or a
/// microphone, converted to 16 kHz mono PCM16 with <see cref="PcmConverter"/>.
/// </summary>
public sealed class WindowsAudioCaptureService : IAudioCaptureService
{
    private const int MicBufferMilliseconds = 50;
    private const int MaxFillBlocksPerTick = 10;                                  // at most 1 s of silence per tick
    private const int FillBlockBytes = AudioFormat.BytesPerSecond / 10;           // 100 ms of 16 kHz mono PCM16

    private static readonly TimeSpan FillInterval = TimeSpan.FromMilliseconds(100);
    private static readonly long FillBlockTicks = Stopwatch.Frequency / 10;           // 100 ms
    private static readonly long FillStartTicks = Stopwatch.Frequency * 150 / 1000;   // 150 ms without real data
    private static readonly long MaxFillTicks = Stopwatch.Frequency;                  // 1 s

    // HRESULTs worth a friendlier message than "Exception from HRESULT: 0x…".
    private const uint E_ACCESSDENIED = 0x80070005;
    private const uint E_NOTFOUND = 0x80070490;
    private const uint AUDCLNT_E_DEVICE_INVALIDATED = 0x88890004;
    private const uint AUDCLNT_E_UNSUPPORTED_FORMAT = 0x88890008;
    private const uint AUDCLNT_E_DEVICE_IN_USE = 0x8889000A;
    private const uint AUDCLNT_E_RESOURCES_INVALIDATED = 0x88890026;

    // Guards _session and everything the capture thread and the silence-fill timer share
    // (converter, timestamps). Chunks are raised under it so silence and real audio stay in order.
    private readonly object _sync = new();
    private readonly SemaphoreSlim _startStopGate = new(1, 1);
    private CaptureSession? _session;
    private volatile bool _disposed;

    public bool SupportsSystemAudio => true;

    public bool IsCapturing => Volatile.Read(ref _session) is not null;

    public event EventHandler<AudioChunkEventArgs>? ChunkAvailable;

    public event EventHandler<string>? Error;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            AddDevices(enumerator, AudioSourceKind.SystemAudio, devices);
            AddDevices(enumerator, AudioSourceKind.Microphone, devices);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio device enumeration failed: {ex}");
        }
        return devices;
    }

    public async Task StartAsync(AudioSourceKind kind, string? deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // No ConfigureAwait(false): the capture objects are created on the calling (UI) thread.
        await _startStopGate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            var session = CreateSession(kind, deviceId);
            session.Capture.DataAvailable += (_, e) => OnDataAvailable(session, e);
            session.Capture.RecordingStopped += (_, e) => OnRecordingStopped(session, e);
            if (kind == AudioSourceKind.SystemAudio)
                session.FillTimer = new Timer(_ => OnFillTick(session), null, FillInterval, FillInterval);

            lock (_sync)
            {
                session.CoveredUntil = Stopwatch.GetTimestamp();
                _session = session;
            }

            try
            {
                session.Capture.StartRecording();
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_session, session))
                        _session = null;
                }
                session.Dispose();
                if (ex is InvalidOperationException)
                    throw;
                throw new InvalidOperationException(DescribeStartFailure(ex, kind), ex);
            }

            lock (_sync)
            {
                // Device initialisation can take a while; measure the silence gap from when capture actually runs.
                if (ReferenceEquals(_session, session))
                    session.CoveredUntil = Stopwatch.GetTimestamp();
            }
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startStopGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        CaptureSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        session?.Dispose();
    }

    private async Task StopCoreAsync()
    {
        CaptureSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }

        // Disposing joins NAudio's capture thread; keep that wait off the UI thread.
        if (session is not null)
            await Task.Run(session.Dispose);
    }

    private void OnDataAvailable(CaptureSession session, WaveInEventArgs e)
    {
        int count = Math.Min(e.BytesRecorded, e.Buffer.Length);
        if (count <= 0)
            return;

        lock (_sync)
        {
            if (!ReferenceEquals(_session, session))
                return;

            session.CoveredUntil = Stopwatch.GetTimestamp();
            session.Filling = false;

            // NAudio reuses e.Buffer, so it is converted here, synchronously.
            var pcm = session.Converter.ConvertBytes(e.Buffer.AsSpan(0, count), session.SampleFormat);
            if (pcm.Length > 0)
                RaiseChunk(pcm, PcmConverter.ComputeLevel(pcm));
        }
    }

    // WASAPI loopback delivers nothing while no app is playing sound. Deepgram needs a continuous stream for its
    // endpointing (and to keep the connection busy), so gaps are filled with zeros until real audio resumes.
    private void OnFillTick(CaptureSession session)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session))
                return;

            long now = Stopwatch.GetTimestamp();
            long gap = now - session.CoveredUntil;
            if (gap < (session.Filling ? FillBlockTicks : FillStartTicks))
                return;

            if (!session.Filling)
            {
                session.Filling = true;
                // Audio after the gap should start from rest, matching the zeros sent in between.
                session.Converter.Reset();
            }

            long blocks = Math.Min(gap / FillBlockTicks, MaxFillBlocksPerTick);
            // After a long stall (timer starvation, system sleep) don't replay the backlog; resume from now.
            session.CoveredUntil = gap > MaxFillTicks ? now : session.CoveredUntil + blocks * FillBlockTicks;

            for (long i = 0; i < blocks; i++)
                RaiseChunk(new byte[FillBlockBytes], 0f);
        }
    }

    private void OnRecordingStopped(CaptureSession session, StoppedEventArgs e)
    {
        // A null exception means we asked it to stop.
        if (e.Exception is null)
            return;

        lock (_sync)
        {
            if (!ReferenceEquals(_session, session))
                return;
            _session = null;
        }

        _ = Task.Run(session.Dispose);
        Error?.Invoke(this, $"Audio capture stopped: {Describe(e.Exception, session.Kind)}");
    }

    // Called under _sync (see field comment); subscribers must not block.
    private void RaiseChunk(byte[] pcm, float level)
    {
        try
        {
            ChunkAvailable?.Invoke(this, new AudioChunkEventArgs(pcm, level));
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not kill the capture thread or crash the process from the timer.
            Debug.WriteLine($"ChunkAvailable handler failed: {ex}");
        }
    }

    private static CaptureSession CreateSession(AudioSourceKind kind, string? deviceId)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiCapture? capture = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = ResolveDevice(enumerator, kind, deviceId)
                ?? throw new InvalidOperationException(
                    kind == AudioSourceKind.SystemAudio ? "No audio output device found" : "No microphone found");

            capture = kind == AudioSourceKind.SystemAudio
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: MicBufferMilliseconds);

            var waveFormat = capture.WaveFormat;
            var sampleFormat = DetectSampleFormat(waveFormat);
            var converter = new PcmConverter(waveFormat.SampleRate, waveFormat.Channels);
            return new CaptureSession(enumerator, device, capture, kind, sampleFormat, converter);
        }
        catch (Exception ex)
        {
            DisposeQuietly(capture);
            DisposeQuietly(device);
            DisposeQuietly(enumerator);
            if (ex is InvalidOperationException)
                throw;
            throw new InvalidOperationException(DescribeStartFailure(ex, kind), ex);
        }
    }

    /// <summary>The requested device if it is still present, active and of the right kind; otherwise the default.</summary>
    private static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, AudioSourceKind kind, string? deviceId)
    {
        var flow = FlowOf(kind);
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            MMDevice? requested = null;
            try
            {
                requested = enumerator.GetDevice(deviceId);
                if (requested.State == DeviceState.Active && requested.DataFlow == flow)
                    return requested;
            }
            catch (Exception ex)
            {
                // Unknown ids surface as ArgumentException (E_INVALIDARG), removed devices as COMException.
                Debug.WriteLine($"Audio device {deviceId} unavailable, using the default: {ex.Message}");
            }
            DisposeQuietly(requested);
        }

        return GetDefaultDevice(enumerator, kind) ?? GetFirstActiveDevice(enumerator, flow);
    }

    private static MMDevice? GetDefaultDevice(MMDeviceEnumerator enumerator, AudioSourceKind kind)
    {
        var flow = FlowOf(kind);
        var preferred = kind == AudioSourceKind.SystemAudio ? Role.Multimedia : Role.Communications;
        ReadOnlySpan<Role> roles = [preferred, Role.Console];
        foreach (var role in roles)
        {
            try
            {
                if (enumerator.HasDefaultAudioEndpoint(flow, role))
                    return enumerator.GetDefaultAudioEndpoint(flow, role);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"No default {flow} endpoint for {role}: {ex.Message}");
            }
        }
        return null;
    }

    private static MMDevice? GetFirstActiveDevice(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            return devices.Count > 0 ? devices[0] : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerating {flow} devices failed: {ex.Message}");
            return null;
        }
    }

    private static void AddDevices(MMDeviceEnumerator enumerator, AudioSourceKind kind, List<AudioDeviceInfo> devices)
    {
        string? defaultId = null;
        try
        {
            using var defaultDevice = GetDefaultDevice(enumerator, kind);
            defaultId = defaultDevice?.ID;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading the default {kind} device failed: {ex.Message}");
        }

        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(FlowOf(kind), DeviceState.Active))
            {
                try
                {
                    string id = device.ID;
                    devices.Add(new AudioDeviceInfo(id, device.FriendlyName, kind, id == defaultId));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Skipping an unreadable {kind} device: {ex.Message}");
                }
                finally
                {
                    DisposeQuietly(device);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerating {kind} devices failed: {ex.Message}");
        }
    }

    /// <summary>Maps the device mix format (plain or WAVE_FORMAT_EXTENSIBLE) to a converter sample format.</summary>
    private static AudioSampleFormat DetectSampleFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
        {
            isFloat = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
            isPcm = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
        }

        // Container size, not valid bits: 24-bit audio in 32-bit containers is left-justified and reads as Int32.
        int channels = format.Channels;
        int bytesPerSample = channels > 0 && format.BlockAlign % channels == 0 ? format.BlockAlign / channels : 0;

        AudioSampleFormat? sampleFormat = (isFloat, isPcm, bytesPerSample) switch
        {
            (true, _, 4) => AudioSampleFormat.Float32,
            (_, true, 2) => AudioSampleFormat.Int16,
            (_, true, 3) => AudioSampleFormat.Int24,
            (_, true, 4) => AudioSampleFormat.Int32,
            _ => null,
        };

        if (sampleFormat is null || format.SampleRate <= 0)
            throw new InvalidOperationException($"This audio device uses a format the app can't read ({format}). Pick another device in Settings.");
        return sampleFormat.Value;
    }

    private static DataFlow FlowOf(AudioSourceKind kind) =>
        kind == AudioSourceKind.SystemAudio ? DataFlow.Render : DataFlow.Capture;

    private static string DescribeStartFailure(Exception ex, AudioSourceKind kind)
    {
        string what = kind == AudioSourceKind.SystemAudio ? "system audio" : "the microphone";
        return $"Could not capture {what}: {Describe(ex, kind)}";
    }

    private static string Describe(Exception ex, AudioSourceKind kind) => unchecked((uint)ex.HResult) switch
    {
        E_ACCESSDENIED when kind == AudioSourceKind.Microphone =>
            "Windows is blocking microphone access. Turn on \"Let desktop apps access your microphone\" in Settings > Privacy & security > Microphone.",
        E_ACCESSDENIED => "Windows denied access to the audio device.",
        E_NOTFOUND => "The audio device was not found.",
        AUDCLNT_E_DEVICE_INVALIDATED => "The audio device was unplugged or disabled.",
        AUDCLNT_E_UNSUPPORTED_FORMAT => "The audio device's format isn't supported.",
        AUDCLNT_E_DEVICE_IN_USE => "Another app is using the audio device exclusively.",
        AUDCLNT_E_RESOURCES_INVALIDATED => "The audio device was reconfigured. Start again.",
        _ => ex.Message,
    };

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disposing {disposable?.GetType().Name} failed: {ex.Message}");
        }
    }

    /// <summary>One device opened by StartAsync. Mutable fields are guarded by the service's lock.</summary>
    private sealed class CaptureSession(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        WasapiCapture capture,
        AudioSourceKind kind,
        AudioSampleFormat sampleFormat,
        PcmConverter converter)
    {
        public WasapiCapture Capture { get; } = capture;

        public AudioSourceKind Kind { get; } = kind;

        public AudioSampleFormat SampleFormat { get; } = sampleFormat;

        public PcmConverter Converter { get; } = converter;

        public Timer? FillTimer { get; set; }

        /// <summary>Stopwatch timestamp up to which the stream has been delivered (real audio or silence).</summary>
        public long CoveredUntil { get; set; }

        public bool Filling { get; set; }

        public void Dispose()
        {
            DisposeQuietly(FillTimer);
            try
            {
                Capture.StopRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopRecording failed: {ex.Message}");
            }
            DisposeQuietly(Capture);
            DisposeQuietly(device);
            DisposeQuietly(enumerator);
        }
    }
}
