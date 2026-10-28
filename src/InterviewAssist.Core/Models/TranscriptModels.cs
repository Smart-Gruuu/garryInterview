namespace InterviewAssist.Core.Models;

public enum TranscriptionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error,
}

public sealed record TranscriptionOptions(
    string ApiKey,
    string Model,
    string Language,
    int EndpointingMs,
    IReadOnlyList<string> KeyTerms);

/// <summary>
/// One Deepgram "Results" message. <see cref="StreamId"/> increases by one per WebSocket (re)connection,
/// and <see cref="Start"/>/<see cref="Duration"/> are seconds relative to the start of that stream.
/// </summary>
public sealed record TranscriptResult(
    int StreamId,
    string Text,
    bool IsFinal,
    bool SpeechFinal,
    bool FromFinalize,
    double Start,
    double Duration)
{
    public double End => Start + Duration;
}

/// <summary>
/// A finalized piece of transcript. <see cref="Sequence"/> is assigned by the store, increases with every segment
/// added, and is never reused (not even after Clear), so it identifies "segments newer than X".
/// </summary>
public sealed record TranscriptSegment(int StreamId, double Start, double End, string Text, DateTimeOffset ReceivedAt, long Sequence = 0);

/// <summary>The pending (unanswered) part of the transcript: finals after the question marker, oldest first, plus the live interim text.</summary>
public sealed record PendingTranscript(IReadOnlyList<TranscriptSegment> Finals, string Interim)
{
    public static readonly PendingTranscript Empty = new([], "");
}

/// <summary>
/// What the transcript view shows: History = text already used for earlier questions (dimmed),
/// Pending = finalized text since the last question, Interim = the live, not-yet-final hypothesis (pending part only).
/// </summary>
public sealed record TranscriptSnapshot(string History, string Pending, string Interim)
{
    public bool HasPending => Pending.Length > 0 || Interim.Length > 0;
}
