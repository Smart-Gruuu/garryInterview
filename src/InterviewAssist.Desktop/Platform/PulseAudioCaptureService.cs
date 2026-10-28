using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Audio;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Desktop.Platform;

/// <summary>
/// Linux capture through the PulseAudio "simple" API (works on PipeWire through pipewire-pulse). System audio is the
/// monitor source of an output (sink); the server resamples and down-mixes to 16 kHz mono PCM16, so no conversion is
/// needed here. Monitor sources stream continuously (zeros while nothing plays), so no silence filling either.
/// </summary>
public sealed class PulseAudioCaptureService : IAudioCaptureService
{
    private const string AppName = "Interview Assist";
    private const int ReadBytes = AudioFormat.BytesPerSecond / 10; // 100 ms
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PactlTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);

    private const string MissingLibraryMessage =
        "Linux audio needs the PulseAudio client library. Install libpulse0 (Debian/Ubuntu) or pulseaudio-libs (Fedora).";

    private readonly SemaphoreSlim _startStopGate = new(1, 1);
    private CaptureSession? _session;
    private volatile bool _disposed;

    public bool SupportsSystemAudio => true;

    public bool IsCapturing => Volatile.Read(ref _session) is not null;

    public event EventHandler<AudioChunkEventArgs>? ChunkAvailable;

    public event EventHandler<string>? Error;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        try
        {
            // Called on the UI thread; the runner skips the remaining pactl calls after one times out.
            var pactl = new PactlRunner();
            var sinks = ListObjects(pactl, "sinks");
            if (sinks is null)
            {
                // pactl isn't installed (or doesn't work): the server defaults still do.
                return PactlOutputParser.FallbackDevices;
            }

            var sources = ListObjects(pactl, "sources") ?? [];
            if (pactl.TimedOut)
            {
                return PactlOutputParser.FallbackDevices;
            }

            // `pactl info` reports both defaults on every pactl version (get-default-sink/-source need pactl 15+).
            var info = pactl.Run("info");
            var defaultSink = info is null ? null : PactlOutputParser.ParseInfoField(info, "Default Sink");
            var defaultSource = info is null ? null : PactlOutputParser.ParseInfoField(info, "Default Source");
            return PactlOutputParser.BuildDevices(sinks, sources, defaultSink, defaultSource);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PulseAudio] Device enumeration failed: {ex}");
            return PactlOutputParser.FallbackDevices;
        }
    }

    public async Task StartAsync(AudioSourceKind kind, string? deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _startStopGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            // pa_simple_new blocks while it connects to the server.
            var handle = await Task.Run(() => Open(kind, deviceId), ct).ConfigureAwait(false);

            var session = new CaptureSession(handle);
            if (_disposed || ct.IsCancellationRequested)
            {
                session.FreeHandle();
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
            }

            Volatile.Write(ref _session, session);
            session.Thread = new Thread(() => ReadLoop(session)) { IsBackground = true, Name = "PulseAudio capture" };
            session.Thread.Start();
            session.StartWatchdog(CheckForStall, StallCheckInterval);

            // Dispose may have run between the check above and publishing the session.
            if (_disposed)
            {
                Interlocked.CompareExchange(ref _session, null, session);
                session.RequestStop();
            }
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
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _session, null)?.RequestStop();
    }

    private async Task StopCoreAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        session.RequestStop();

        // The read thread frees the stream itself once its current read (at most ~100 ms) returns. If the server
        // stalls, give up waiting: the thread still frees it whenever the read comes back.
        var thread = session.Thread;
        if (thread is not null)
        {
            await Task.Run(() => thread.Join(StopTimeout)).ConfigureAwait(false);
        }
    }

    private static nint Open(AudioSourceKind kind, string? deviceId)
    {
        // A sink's monitor is normally "<sink>.monitor", but PulseAudio renames it on a name collision; pactl reports
        // the real name. This runs off the UI thread, so the lookup's process launch doesn't freeze anything.
        var monitorSources = PactlOutputParser.IsSinkName(kind, deviceId)
            ? PactlOutputParser.BuildMonitorSourceMap(ListObjects(new PactlRunner(), "sinks") ?? [])
            : null;

        var device = PactlOutputParser.ResolveCaptureDevice(kind, deviceId, monitorSources);
        try
        {
            var handle = TryOpen(device, kind, out var error);
            if (handle != 0)
            {
                return handle;
            }

            // The chosen device may have been unplugged or renamed: fall back to the default one.
            var defaultDevice = PactlOutputParser.ResolveCaptureDevice(kind, null);
            if (!string.Equals(device, defaultDevice, StringComparison.Ordinal))
            {
                Debug.WriteLine($"[PulseAudio] Opening {device} failed ({Describe(error)}); using the default device.");
                handle = TryOpen(defaultDevice, kind, out error);
                if (handle != 0)
                {
                    return handle;
                }
            }

            var what = kind == AudioSourceKind.SystemAudio ? "system audio" : "the microphone";
            throw new InvalidOperationException($"Could not capture {what}: {Describe(error)}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(MissingLibraryMessage, ex);
        }
    }

    private static unsafe nint TryOpen(string? device, AudioSourceKind kind, out int error)
    {
        var spec = new PulseNative.PaSampleSpec
        {
            format = PulseNative.PA_SAMPLE_S16LE,
            rate = AudioFormat.SampleRate,
            channels = AudioFormat.Channels,
        };

        // (uint32_t)-1 lets the server choose; tlength/prebuf/minreq only apply to playback.
        var attributes = new PulseNative.PaBufferAttr
        {
            maxlength = uint.MaxValue,
            tlength = uint.MaxValue,
            prebuf = uint.MaxValue,
            minreq = uint.MaxValue,
            fragsize = ReadBytes,
        };

        var streamName = kind == AudioSourceKind.SystemAudio ? "System audio" : "Microphone";
        int code = 0;
        var handle = PulseNative.pa_simple_new(null, AppName, PulseNative.PA_STREAM_RECORD, device, streamName, &spec, 0, &attributes, &code);
        error = code;
        return handle;
    }

    private void ReadLoop(CaptureSession session)
    {
        string? failure = null;
        try
        {
            while (!session.StopRequested)
            {
                var pcm = new byte[ReadBytes];
                int error;
                int result;
                unsafe
                {
                    fixed (byte* data = pcm)
                    {
                        error = 0;
                        result = PulseNative.pa_simple_read(session.Handle, data, (nuint)pcm.Length, &error);
                    }
                }

                if (session.StopRequested)
                {
                    break;
                }

                if (result < 0)
                {
                    failure = Describe(error);
                    break;
                }

                session.MarkDataReceived();
                RaiseChunk(pcm, PcmConverter.ComputeLevel(pcm));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PulseAudio] Capture thread failed: {ex}");
            failure = ex.Message;
        }
        finally
        {
            session.StopWatchdog();
            session.FreeHandle();
        }

        if (failure is not null && Interlocked.CompareExchange(ref _session, null, session) == session)
        {
            RaiseError($"Audio capture stopped: {failure}");
        }
    }

    /// <summary>
    /// pa_simple_read has no timeout: a suspended source (e.g. `pactl suspend-source`, or an inactive logind session)
    /// blocks it with no error while the connection stays up. Report that instead of letting captions stop silently.
    /// The read thread keeps ownership of the handle and frees it whenever the read returns.
    /// </summary>
    private void CheckForStall(object? state)
    {
        var session = (CaptureSession)state!;
        if (session.StopRequested || session.TimeSinceData < StallTimeout)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _session, null, session) == session)
        {
            session.RequestStop();
            RaiseError($"Audio capture stopped: no audio from the device for {StallTimeout.TotalSeconds:0} seconds. " +
                       "It may have been suspended or disconnected.");
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
            // A faulty subscriber must not kill the capture thread.
            Debug.WriteLine($"[PulseAudio] ChunkAvailable handler failed: {ex}");
        }
    }

    private void RaiseError(string message)
    {
        try
        {
            Error?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PulseAudio] Error handler failed: {ex}");
        }
    }

    private static string Describe(int error)
    {
        string text;
        try
        {
            var message = PulseNative.pa_strerror(error);
            text = message == 0 ? "" : Marshal.PtrToStringUTF8(message) ?? "";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            text = "";
        }

        if (text.Length == 0)
        {
            text = $"PulseAudio error {error}";
        }

        return error switch
        {
            PulseNative.PA_ERR_CONNECTIONREFUSED => $"{text}. Is PulseAudio or PipeWire (pipewire-pulse) running?",
            PulseNative.PA_ERR_NOENTITY => $"{text}. The audio device was not found.",
            PulseNative.PA_ERR_ACCESS => $"{text}. Access to the audio device was denied.",
            _ => text,
        };
    }

    /// <summary>Parsed <c>pactl list sinks|sources</c>, or null when pactl can't be run.</summary>
    private static IReadOnlyList<PactlEntry>? ListObjects(PactlRunner pactl, string objectType)
    {
        var output = pactl.Run("list", objectType);
        if (output is null)
        {
            return null;
        }

        var entries = PactlOutputParser.ParseList(output);
        if (entries.Count > 0)
        {
            return entries;
        }

        // Some pactl builds print nothing useful in long form; the short form always has the names.
        var shortOutput = pactl.Run("list", "short", objectType);
        return shortOutput is null ? entries : PactlOutputParser.ParseShortList(shortOutput);
    }

    /// <summary>
    /// Runs pactl with the C locale (stable field names). A server that accepts the connection but stalls makes every
    /// run wait out its timeout, so after the first timeout the runner skips the rest of its calls.
    /// </summary>
    private sealed class PactlRunner
    {
        public bool TimedOut { get; private set; }

        /// <summary>pactl's output; null on any failure, after a timeout, or once an earlier call timed out.</summary>
        public string? Run(params string[] arguments)
        {
            if (TimedOut)
            {
                return null;
            }

            var startInfo = new ProcessStartInfo("pactl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["LC_ALL"] = "C";

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(PactlTimeout))
                {
                    TimedOut = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PulseAudio] Killing pactl failed: {ex.Message}");
                    }

                    return null;
                }

                if (!Task.WaitAll([output, errors], PactlTimeout))
                {
                    TimedOut = true;
                    return null;
                }

                return process.ExitCode == 0 ? output.Result : null;
            }
            catch (Win32Exception)
            {
                // pactl isn't installed.
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PulseAudio] pactl {string.Join(' ', arguments)} failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>One opened record stream, its read thread and its stall watchdog.</summary>
    private sealed class CaptureSession(nint handle)
    {
        private nint _handle = handle;
        private long _lastDataTicks = Environment.TickCount64;
        private Timer? _watchdog;
        private volatile bool _stopRequested;

        public nint Handle => _handle;

        public Thread? Thread { get; set; }

        public bool StopRequested => _stopRequested;

        public TimeSpan TimeSinceData =>
            TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks));

        public void MarkDataReceived() => Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);

        public void RequestStop()
        {
            _stopRequested = true;
            StopWatchdog();
        }

        /// <summary>Calls <paramref name="check"/> with this session every <paramref name="interval"/> until stopped.</summary>
        public void StartWatchdog(TimerCallback check, TimeSpan interval)
        {
            Interlocked.Exchange(ref _watchdog, new Timer(check, this, interval, interval))?.Dispose();

            // RequestStop may have run before the timer was published.
            if (_stopRequested)
            {
                StopWatchdog();
            }
        }

        public void StopWatchdog() => Interlocked.Exchange(ref _watchdog, null)?.Dispose();

        /// <summary>Frees the stream once. Only called when no read is in progress (by the read thread, or before it starts).</summary>
        public void FreeHandle()
        {
            var handleToFree = Interlocked.Exchange(ref _handle, 0);
            if (handleToFree == 0)
            {
                return;
            }

            try
            {
                PulseNative.pa_simple_free(handleToFree);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PulseAudio] pa_simple_free failed: {ex.Message}");
            }
        }
    }
}

/// <summary>A sink or source reported by pactl. <paramref name="MonitorSource"/>: a sink's monitor source, when listed.</summary>
public sealed record PactlEntry(string Name, string Description, bool IsMonitor, string? MonitorSource = null);

/// <summary>Pure helpers that turn pactl output into devices, and device choices into PulseAudio source names.</summary>
public static class PactlOutputParser
{
    public const string DefaultMonitorSource = "@DEFAULT_MONITOR@";
    public const string DefaultSource = "@DEFAULT_SOURCE@";
    private const string MonitorSuffix = ".monitor";

    /// <summary>What GetDevices returns when pactl isn't available: the server's default devices.</summary>
    public static IReadOnlyList<AudioDeviceInfo> FallbackDevices { get; } =
    [
        new(DefaultMonitorSource, "Default output (what you hear)", AudioSourceKind.SystemAudio, true),
        new(DefaultSource, "Default microphone", AudioSourceKind.Microphone, true),
    ];

    /// <summary>
    /// Parses <c>LC_ALL=C pactl list sinks</c> / <c>sources</c>: blocks start with "Sink #N" / "Source #N" and hold
    /// one-tab-indented "Name:", "Description:", (sinks) "Monitor Source:" and (sources) "Monitor of Sink:" lines.
    /// </summary>
    public static IReadOnlyList<PactlEntry> ParseList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var entries = new List<PactlEntry>();
        string? name = null;
        string? description = null;
        string? monitorSource = null;
        var monitorOfSink = false;
        var inBlock = false;

        void Flush()
        {
            if (inBlock && !string.IsNullOrEmpty(name))
            {
                var isMonitor = monitorOfSink || name.EndsWith(MonitorSuffix, StringComparison.Ordinal);
                entries.Add(new PactlEntry(
                    name, string.IsNullOrWhiteSpace(description) ? name : description, isMonitor, monitorSource));
            }

            name = null;
            description = null;
            monitorSource = null;
            monitorOfSink = false;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                Flush();
                inBlock = line.StartsWith("Sink #", StringComparison.Ordinal) || line.StartsWith("Source #", StringComparison.Ordinal);
                continue;
            }

            // Only the block's own fields (one tab); deeper lines are properties, ports and formats.
            if (!inBlock || line.Length < 2 || line[0] != '\t' || line[1] == '\t')
            {
                continue;
            }

            var field = line.AsSpan(1);
            if (TryReadField(field, "Name:", out var value))
            {
                name ??= value;
            }
            else if (TryReadField(field, "Description:", out value))
            {
                description ??= value;
            }
            else if (TryReadField(field, "Monitor of Sink:", out value))
            {
                monitorOfSink = value.Length > 0 && value != "n/a";
            }
            else if (TryReadField(field, "Monitor Source:", out value) && value.Length > 0 && value != "n/a")
            {
                monitorSource ??= value;
            }
        }

        Flush();
        return entries;
    }

    /// <summary>Parses <c>pactl list short sinks|sources</c>: tab-separated index, name, driver, sample spec, state.</summary>
    public static IReadOnlyList<PactlEntry> ParseShortList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var entries = new List<PactlEntry>();
        foreach (var rawLine in output.Split('\n'))
        {
            var columns = rawLine.TrimEnd('\r').Split('\t');
            if (columns.Length < 2)
            {
                continue;
            }

            var name = columns[1].Trim();
            if (name.Length > 0)
            {
                entries.Add(new PactlEntry(name, name, name.EndsWith(MonitorSuffix, StringComparison.Ordinal)));
            }
        }

        return entries;
    }

    /// <summary>The value of a "Field: value" line in <c>pactl info</c> (e.g. "Default Sink"); null when absent or empty.</summary>
    public static string? ParseInfoField(string output, string field)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(field);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (TryReadField(line, field + ":", out var value))
            {
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Sinks become system-audio devices (their id is the sink name), non-monitor sources become microphones.
    /// <paramref name="defaultSink"/>/<paramref name="defaultSource"/> mark the defaults.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> BuildDevices(
        IReadOnlyList<PactlEntry> sinks,
        IReadOnlyList<PactlEntry> sources,
        string? defaultSink,
        string? defaultSource)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(sources);

        var devices = new List<AudioDeviceInfo>(sinks.Count + sources.Count);
        foreach (var sink in sinks)
        {
            devices.Add(new AudioDeviceInfo(sink.Name, sink.Description, AudioSourceKind.SystemAudio,
                string.Equals(sink.Name, defaultSink, StringComparison.Ordinal)));
        }

        foreach (var source in sources)
        {
            if (source.IsMonitor)
            {
                continue;
            }

            devices.Add(new AudioDeviceInfo(source.Name, source.Description, AudioSourceKind.Microphone,
                string.Equals(source.Name, defaultSource, StringComparison.Ordinal)));
        }

        return devices;
    }

    /// <summary>Sink name to monitor source name, for the sinks whose monitor pactl listed.</summary>
    public static IReadOnlyDictionary<string, string> BuildMonitorSourceMap(IReadOnlyList<PactlEntry> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sink in sinks)
        {
            if (!string.IsNullOrEmpty(sink.MonitorSource))
            {
                map.TryAdd(sink.Name, sink.MonitorSource);
            }
        }

        return map;
    }

    /// <summary>True when <paramref name="deviceId"/> is a sink chosen for system audio, i.e. its monitor must be looked up.</summary>
    public static bool IsSinkName(AudioSourceKind kind, string? deviceId)
    {
        var id = deviceId?.Trim();
        return kind == AudioSourceKind.SystemAudio
            && !string.IsNullOrEmpty(id)
            && !id.StartsWith('@')
            && !id.EndsWith(MonitorSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PulseAudio source to record from. System audio: the monitor of the chosen sink, as listed in
    /// <paramref name="monitorSources"/> or else "&lt;sink&gt;.monitor"; default "@DEFAULT_MONITOR@". Microphone: the
    /// chosen source, or null for the server's default source. Special names ("@…@") and explicit ".monitor" sources
    /// are used as they are.
    /// </summary>
    public static string? ResolveCaptureDevice(
        AudioSourceKind kind,
        string? deviceId,
        IReadOnlyDictionary<string, string>? monitorSources = null)
    {
        var id = deviceId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return kind == AudioSourceKind.SystemAudio ? DefaultMonitorSource : null;
        }

        if (!IsSinkName(kind, id))
        {
            return id;
        }

        return monitorSources is not null && monitorSources.TryGetValue(id, out var monitor) ? monitor : id + MonitorSuffix;
    }

    private static bool TryReadField(ReadOnlySpan<char> line, string field, out string value)
    {
        if (line.StartsWith(field, StringComparison.Ordinal))
        {
            value = line[field.Length..].Trim().ToString();
            return true;
        }

        value = "";
        return false;
    }
}

/// <summary>
/// PulseAudio interop for Linux x64. pa_simple_* from pulse/simple.h (libpulse-simple), pa_strerror from
/// pulse/error.h (libpulse), types from pulse/sample.h and pulse/def.h. size_t is nuint; enums are int.
/// </summary>
internal static class PulseNative
{
    public const string SimpleLibrary = "libpulse-simple.so.0";
    public const string Library = "libpulse.so.0";

    // pulse/sample.h, enum pa_sample_format: U8, ALAW, ULAW, S16LE (implicit values from 0).
    public const int PA_SAMPLE_S16LE = 3;

    // pulse/def.h, enum pa_stream_direction: NODIRECTION, PLAYBACK, RECORD, UPLOAD.
    public const int PA_STREAM_RECORD = 2;

    // pulse/def.h, enum pa_error_code: PA_OK = 0, ACCESS, COMMAND, INVALID, EXIST, NOENTITY, CONNECTIONREFUSED, ...
    public const int PA_ERR_ACCESS = 1;
    public const int PA_ERR_NOENTITY = 5;
    public const int PA_ERR_CONNECTIONREFUSED = 6;

    /// <summary>pa_sample_spec: pa_sample_format_t format; uint32_t rate; uint8_t channels (12 bytes with padding).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PaSampleSpec
    {
        public int format;
        public uint rate;
        public byte channels;
    }

    /// <summary>pa_buffer_attr: five uint32_t in this order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PaBufferAttr
    {
        public uint maxlength;
        public uint tlength;
        public uint prebuf;
        public uint minreq;
        public uint fragsize;
    }

    // pa_simple* pa_simple_new(const char *server, const char *name, pa_stream_direction_t dir, const char *dev,
    //     const char *stream_name, const pa_sample_spec *ss, const pa_channel_map *map, const pa_buffer_attr *attr,
    //     int *error);
    [DllImport(SimpleLibrary)]
    public static extern unsafe nint pa_simple_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? dev,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string streamName,
        PaSampleSpec* ss,
        nint map,
        PaBufferAttr* attr,
        int* error);

    // void pa_simple_free(pa_simple *s);
    [DllImport(SimpleLibrary)]
    public static extern void pa_simple_free(nint s);

    // int pa_simple_read(pa_simple *s, void *data, size_t bytes, int *error);
    [DllImport(SimpleLibrary)]
    public static extern unsafe int pa_simple_read(nint s, byte* data, nuint bytes, int* error);

    // const char* pa_strerror(int error);  (static string, not freed)
    [DllImport(Library)]
    public static extern nint pa_strerror(int error);
}
