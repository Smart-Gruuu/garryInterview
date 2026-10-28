using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Presentation.Tests.Fakes;

public sealed class FakeAudioCaptureService : IAudioCaptureService
{
    public bool SupportsSystemAudio { get; set; } = true;

    public bool IsCapturing { get; private set; }

    public List<AudioDeviceInfo> Devices { get; } = [];

    /// <summary>When set, <see cref="StartAsync"/> fails with it.</summary>
    public Exception? StartException { get; set; }

    public List<(AudioSourceKind Kind, string? DeviceId)> Starts { get; } = [];

    public int StopCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public event EventHandler<AudioChunkEventArgs>? ChunkAvailable;

    public event EventHandler<string>? Error;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [.. Devices];

    public Task StartAsync(AudioSourceKind kind, string? deviceId, CancellationToken ct = default)
    {
        Starts.Add((kind, deviceId));
        if (StartException is not null)
        {
            return Task.FromException(StartException);
        }

        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public void RaiseChunk(byte[] pcm16, float level) => ChunkAvailable?.Invoke(this, new AudioChunkEventArgs(pcm16, level));

    public void RaiseError(string message) => Error?.Invoke(this, message);

    public void Dispose() => IsDisposed = true;
}
