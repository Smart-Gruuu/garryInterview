using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Abstractions;

/// <summary>
/// Captures audio from a device and delivers it as 16 kHz mono PCM16 (<see cref="AudioFormat"/>).
/// Implemented per platform (Windows: WASAPI loopback + microphone; Android: microphone).
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    /// <summary>True when <see cref="AudioSourceKind.SystemAudio"/> capture is available (Windows).</summary>
    bool SupportsSystemAudio { get; }

    bool IsCapturing { get; }

    /// <summary>Active devices that can be captured. Never throws; returns an empty list on failure.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>
    /// Starts capturing. <paramref name="deviceId"/> null/empty = the system default device of <paramref name="kind"/>
    /// (falls back to the default if the id is no longer present). Stops any capture already running first.
    /// Throws <see cref="InvalidOperationException"/> with a user-facing message if the device can't be opened
    /// or permission is denied.
    /// </summary>
    Task StartAsync(AudioSourceKind kind, string? deviceId, CancellationToken ct = default);

    /// <summary>Stops capturing. Safe to call when not capturing.</summary>
    Task StopAsync();

    /// <summary>Raised on a background thread, roughly every 20–100 ms while capturing.</summary>
    event EventHandler<AudioChunkEventArgs>? ChunkAvailable;

    /// <summary>Capture failed after starting (device unplugged, etc.). User-facing message. Any thread.</summary>
    event EventHandler<string>? Error;
}
