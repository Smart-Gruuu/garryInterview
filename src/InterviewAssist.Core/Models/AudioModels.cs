namespace InterviewAssist.Core.Models;

public enum AudioSourceKind
{
    /// <summary>What the computer is playing (the interviewer's voice in Zoom/Teams/Meet). Windows: WASAPI loopback.</summary>
    SystemAudio,

    /// <summary>A microphone. The only option on Android.</summary>
    Microphone,
}

public sealed record AudioDeviceInfo(string Id, string Name, AudioSourceKind Kind, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>The single audio format that flows from capture to transcription.</summary>
public static class AudioFormat
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerSecond = SampleRate * Channels * BitsPerSample / 8;
}

public sealed class AudioChunkEventArgs(byte[] pcm16, float level) : EventArgs
{
    /// <summary>Little-endian signed 16-bit PCM, mono, 16 kHz (see <see cref="AudioFormat"/>).</summary>
    public byte[] Pcm16 { get; } = pcm16;

    /// <summary>RMS level of this chunk, 0..1, for a level meter.</summary>
    public float Level { get; } = level;
}
