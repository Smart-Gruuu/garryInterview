using System.Diagnostics;
using Android.Media;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Audio;
using InterviewAssist.Core.Models;
using AudioDeviceInfo = InterviewAssist.Core.Models.AudioDeviceInfo;
using PcmFormat = InterviewAssist.Core.Models.AudioFormat;

namespace InterviewAssist.Native;

/// <summary>
/// Microphone capture with <see cref="AudioRecord"/>, recorded directly in the transport format
/// (16 kHz mono PCM16), so chunks are passed on as-is. Android can't capture other apps' audio.
/// </summary>
public sealed class AndroidAudioCaptureService : IAudioCaptureService
{
    private const int BlockSamples = 640;                                            // 40 ms at 16 kHz
    private const int BlockBytes = BlockSamples * PcmFormat.BitsPerSample / 8;
    private const int MinRecorderBufferBytes = 3200 * 2;                             // 200 ms
    private const int ErrorDeadObject = -6;                                          // AudioRecord.ERROR_DEAD_OBJECT

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly AudioDeviceInfo DefaultMicrophone = new("default", "Microphone", AudioSourceKind.Microphone, true);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _startStopGate = new(1, 1);
    private RecordSession? _session;
    private volatile bool _disposed;

    public bool SupportsSystemAudio => false;

    public bool IsCapturing => Volatile.Read(ref _session) is not null;

    public event EventHandler<AudioChunkEventArgs>? ChunkAvailable;

    public event EventHandler<string>? Error;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [DefaultMicrophone];

    public async Task StartAsync(AudioSourceKind kind, string? deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _startStopGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            // System audio isn't available here (the UI never offers it); the microphone is used for any kind.
            var permission = await MainThread.InvokeOnMainThreadAsync(RequestMicrophonePermissionAsync).ConfigureAwait(false);
            if (permission != PermissionStatus.Granted)
                throw new InvalidOperationException("Microphone permission is required");

            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            var session = new RecordSession(OpenRecorder());
            lock (_sync)
                _session = session;

            session.ReadLoop = Task.Factory.StartNew(
                () => ReadLoop(session),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startStopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
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

        RecordSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        // The read loop releases the recorder once the pending read returns.
        session?.RequestStop();
    }

    private async Task StopCoreAsync()
    {
        RecordSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        if (session is null)
            return;

        session.RequestStop();
        try
        {
            // Waiting guarantees no ChunkAvailable is raised after StopAsync returns.
            await session.ReadLoop.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("Microphone read loop did not stop in time.");
        }
    }

    private static async Task<PermissionStatus> RequestMicrophonePermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        return status == PermissionStatus.Granted ? status : await Permissions.RequestAsync<Permissions.Microphone>();
    }

    private static AudioRecord OpenRecorder()
    {
        int minBuffer = AudioRecord.GetMinBufferSize(PcmFormat.SampleRate, ChannelIn.Mono, Android.Media.Encoding.Pcm16bit);
        if (minBuffer <= 0)
            throw new InvalidOperationException("This device can't record 16 kHz audio from the microphone.");

        AudioRecord? recorder = null;
        try
        {
            recorder = new AudioRecord(
                AudioSource.VoiceRecognition,
                PcmFormat.SampleRate,
                ChannelIn.Mono,
                Android.Media.Encoding.Pcm16bit,
                Math.Max(minBuffer, MinRecorderBufferBytes));

            if (recorder.State != Android.Media.State.Initialized)
                throw new InvalidOperationException("Could not open the microphone. Another app may be using it.");

            recorder.StartRecording();
            if (recorder.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("Could not start recording. Another app may be using the microphone.");

            return recorder;
        }
        catch (Exception ex)
        {
            if (recorder is not null)
                RecordSession.ReleaseQuietly(recorder);
            if (ex is InvalidOperationException)
                throw;
            throw new InvalidOperationException($"Could not open the microphone: {ex.Message}", ex);
        }
    }

    private void ReadLoop(RecordSession session)
    {
        var buffer = new byte[BlockBytes];
        try
        {
            while (!session.StopRequested)
            {
                // Blocking read; returns whole frames, or early (possibly 0) once Stop() is called.
                int read = session.Recorder.Read(buffer, 0, buffer.Length);
                if (session.StopRequested)
                    break;

                if (read < 0)
                {
                    Fail(session, read == ErrorDeadObject
                        ? "Audio capture stopped: the microphone was taken over by another app or disconnected."
                        : $"Audio capture stopped: the microphone returned error {read}.");
                    break;
                }

                if (read == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                var pcm = buffer.AsSpan(0, read).ToArray();
                RaiseChunk(pcm, PcmConverter.ComputeLevel(pcm));
            }
        }
        catch (Exception ex)
        {
            if (!session.StopRequested)
                Fail(session, $"Audio capture stopped: {ex.Message}");
        }
        finally
        {
            session.Release();
        }
    }

    private void RaiseChunk(byte[] pcm, float level)
    {
        try
        {
            ChunkAvailable?.Invoke(this, new AudioChunkEventArgs(pcm, level));
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not end the recording.
            Debug.WriteLine($"ChunkAvailable handler failed: {ex}");
        }
    }

    private void Fail(RecordSession session, string message)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session))
                return;
            _session = null;
        }
        Error?.Invoke(this, message);
    }

    /// <summary>One opened recorder. Only the read loop releases it, after its last read, so a read never races Release.</summary>
    private sealed class RecordSession(AudioRecord recorder)
    {
        private readonly object _recorderLock = new();
        private volatile bool _stopRequested;
        private bool _released;

        public AudioRecord Recorder { get; } = recorder;

        public Task ReadLoop { get; set; } = Task.CompletedTask;

        public bool StopRequested => _stopRequested;

        public void RequestStop()
        {
            _stopRequested = true;
            lock (_recorderLock)
            {
                if (_released)
                    return;
                try
                {
                    // Also makes a blocked Read return.
                    Recorder.Stop();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AudioRecord.Stop failed: {ex.Message}");
                }
            }
        }

        public void Release()
        {
            lock (_recorderLock)
            {
                if (_released)
                    return;
                _released = true;
                ReleaseQuietly(Recorder);
            }
        }

        public static void ReleaseQuietly(AudioRecord recorder)
        {
            try
            {
                recorder.Release();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AudioRecord.Release failed: {ex.Message}");
            }
            recorder.Dispose();
        }
    }
}
