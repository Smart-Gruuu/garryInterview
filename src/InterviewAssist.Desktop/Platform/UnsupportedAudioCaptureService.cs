using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Desktop.Platform;

/// <summary>Audio capture for operating systems the desktop app has no capture backend for (e.g. macOS).</summary>
public sealed class UnsupportedAudioCaptureService : IAudioCaptureService
{
    public bool SupportsSystemAudio => false;

    public bool IsCapturing => false;

    // Never raised: nothing is ever captured.
    public event EventHandler<AudioChunkEventArgs>? ChunkAvailable
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Error
    {
        add { }
        remove { }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

    public Task StartAsync(AudioSourceKind kind, string? deviceId, CancellationToken ct = default) =>
        Task.FromException(new InvalidOperationException(
            "Audio capture isn't supported on this operating system yet. Interview Assist runs on Linux and Windows."));

    public Task StopAsync() => Task.CompletedTask;

    public void Dispose()
    {
    }
}
