using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Abstractions;

/// <summary>
/// Accumulates live transcript results and tracks the "question marker": everything received after the
/// marker is the pending question. All members are thread-safe.
/// </summary>
public interface ITranscriptStore
{
    /// <summary>Applies a result: finals are appended as segments, interims replace the current interim.</summary>
    void Apply(TranscriptResult result);

    /// <summary>Finalized segments in arrival order (snapshot copy).</summary>
    IReadOnlyList<TranscriptSegment> GetSegments();

    /// <summary>Text for display; History is tail-truncated to roughly <paramref name="maxHistoryChars"/>.</summary>
    TranscriptSnapshot GetSnapshot(int maxHistoryChars = 3000);

    /// <summary>Finalized + interim text after the marker, without consuming it.</summary>
    string GetPendingText();

    /// <summary>The pending finals (with their sequence numbers) and interim, read atomically, without consuming them.</summary>
    PendingTranscript GetPending();

    /// <summary>Like <see cref="GetPending"/>, then advances the marker exactly as <see cref="TakePendingQuestion"/> does.</summary>
    PendingTranscript TakePending();

    /// <summary>
    /// Returns the pending text (finals after the marker + the pending interim) and moves the marker past
    /// everything received so far, including the interim, so a later final for that same audio is not
    /// counted again. Returns "" when nothing is pending (marker is still advanced).
    /// </summary>
    string TakePendingQuestion();

    /// <summary>
    /// The last <paramref name="maxChars"/> characters of finalized transcript that come BEFORE the marker
    /// (context for the LLM), trimmed to a word boundary. "" when there is none.
    /// </summary>
    string GetRecentContext(int maxChars);

    void Clear();

    /// <summary>Raised after every mutation. Any thread.</summary>
    event EventHandler? Changed;
}
