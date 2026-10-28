using System.Text;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Transcription;

/// <summary>
/// Thread-safe live transcript with a "question marker" (stream id + time). Finals and the interim that start
/// after the marker are the pending question; everything before it is history.
/// </summary>
public sealed class TranscriptStore : ITranscriptStore
{
    internal const int MaxSegments = 2000;

    // Deepgram's timestamps for the same audio can shift slightly between interim and final results.
    private const double MarkerTolerance = 0.05;
    private const string Ellipsis = "… ";

    private readonly object _gate = new();
    private readonly List<TranscriptSegment> _segments = [];
    private TranscriptResult? _interim;
    private int _markerStreamId;
    private double _markerTime;
    private int _latestStreamId;
    private long _lastSequence;

    public event EventHandler? Changed;

    public void Apply(TranscriptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = (result.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (result.StreamId > _latestStreamId)
            {
                _latestStreamId = result.StreamId;

                // The previous connection ended before this interim was finalized; keep its words.
                if (_interim is { } stale && stale.StreamId < result.StreamId)
                {
                    AddSegment(new TranscriptSegment(stale.StreamId, stale.Start, stale.End, stale.Text, DateTimeOffset.Now));
                    _interim = null;
                }
            }

            if (result.IsFinal)
            {
                AddSegment(new TranscriptSegment(result.StreamId, result.Start, result.End, text, DateTimeOffset.Now));
                if (_interim is { } interim && interim.StreamId == result.StreamId && interim.Start < result.End)
                {
                    _interim = null;
                }
            }
            else
            {
                if (result.StreamId < _latestStreamId)
                {
                    return;
                }

                _interim = result with { Text = text };
            }
        }

        OnChanged();
    }

    public IReadOnlyList<TranscriptSegment> GetSegments()
    {
        lock (_gate)
        {
            return _segments.ToArray();
        }
    }

    /// <summary>
    /// History and Pending/Interim are meant to be shown one after another (e.g. as spans of one label), so
    /// Pending and Interim start with a single space when something precedes them. Each part is "" when empty.
    /// </summary>
    public TranscriptSnapshot GetSnapshot(int maxHistoryChars = 3000)
    {
        string history;
        string pending;
        var interim = "";

        lock (_gate)
        {
            var historyParts = new List<string>();
            var pendingBuilder = new StringBuilder();
            foreach (var segment in _segments)
            {
                if (IsPending(segment.StreamId, segment.Start))
                {
                    AppendNormalized(pendingBuilder, segment.Text);
                }
                else
                {
                    historyParts.Add(segment.Text);
                }
            }

            if (_interim is { } current)
            {
                if (IsPending(current.StreamId, current.Start))
                {
                    interim = current.Text;
                }
                else
                {
                    historyParts.Add(current.Text);
                }
            }

            history = JoinTail(historyParts, maxHistoryChars, out var truncated);
            if (truncated && history.Length > 0)
            {
                history = Ellipsis + history;
            }

            pending = pendingBuilder.ToString();
        }

        if (pending.Length > 0 && history.Length > 0)
        {
            pending = " " + pending;
        }

        if (interim.Length > 0 && (history.Length > 0 || pending.Length > 0))
        {
            interim = " " + interim;
        }

        return new TranscriptSnapshot(history, pending, interim);
    }

    public string GetPendingText()
    {
        lock (_gate)
        {
            return BuildPendingText();
        }
    }

    public string TakePendingQuestion()
    {
        string question;
        lock (_gate)
        {
            question = BuildPendingText();
            AdvanceMarker();
        }

        OnChanged();
        return question;
    }

    public PendingTranscript GetPending()
    {
        lock (_gate)
        {
            return BuildPending();
        }
    }

    public PendingTranscript TakePending()
    {
        PendingTranscript pending;
        lock (_gate)
        {
            pending = BuildPending();
            AdvanceMarker();
        }

        OnChanged();
        return pending;
    }

    public string GetRecentContext(int maxChars)
    {
        if (maxChars <= 0)
        {
            return "";
        }

        lock (_gate)
        {
            var parts = new List<string>();
            foreach (var segment in _segments)
            {
                if (!IsPending(segment.StreamId, segment.Start))
                {
                    parts.Add(segment.Text);
                }
            }

            return JoinTail(parts, maxChars, out _);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Move the marker past everything seen so late finals for cleared audio are not counted again.
            AdvanceMarker();
            _segments.Clear();
            _interim = null;
        }

        OnChanged();
    }

    private bool IsPending(int streamId, double start) =>
        streamId > _markerStreamId
        || (streamId == _markerStreamId && start >= _markerTime - MarkerTolerance);

    private string BuildPendingText()
    {
        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            if (IsPending(segment.StreamId, segment.Start))
            {
                AppendNormalized(builder, segment.Text);
            }
        }

        if (_interim is { } interim && IsPending(interim.StreamId, interim.Start))
        {
            AppendNormalized(builder, interim.Text);
        }

        return builder.ToString();
    }

    private PendingTranscript BuildPending()
    {
        var finals = new List<TranscriptSegment>();
        foreach (var segment in _segments)
        {
            if (IsPending(segment.StreamId, segment.Start))
            {
                finals.Add(segment);
            }
        }

        var interim = _interim is { } current && IsPending(current.StreamId, current.Start) ? current.Text : "";
        return finals.Count == 0 && interim.Length == 0 ? PendingTranscript.Empty : new PendingTranscript(finals, interim);
    }

    /// <summary>Marker = (latest stream, max(end of its finals, interim end)); never moves backwards.</summary>
    private void AdvanceMarker()
    {
        var latest = _latestStreamId;
        if (latest < _markerStreamId)
        {
            return;
        }

        var time = latest == _markerStreamId ? _markerTime : 0;
        foreach (var segment in _segments)
        {
            if (segment.StreamId == latest && segment.End > time)
            {
                time = segment.End;
            }
        }

        if (_interim is { } interim && interim.StreamId == latest && interim.End > time)
        {
            time = interim.End;
        }

        _markerStreamId = latest;
        _markerTime = time;
    }

    private void AddSegment(TranscriptSegment segment)
    {
        _segments.Add(segment with { Sequence = ++_lastSequence });
        if (_segments.Count > MaxSegments)
        {
            _segments.RemoveRange(0, _segments.Count - MaxSegments);
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Appends <paramref name="text"/> with whitespace runs collapsed, space-separated from what's there.</summary>
    private static void AppendNormalized(StringBuilder builder, string text)
    {
        var needSpace = builder.Length > 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                needSpace = builder.Length > 0;
                continue;
            }

            if (needSpace)
            {
                builder.Append(' ');
                needSpace = false;
            }

            builder.Append(c);
        }
    }

    /// <summary>
    /// Joins <paramref name="parts"/> with spaces and keeps at most the last <paramref name="maxChars"/> characters,
    /// cut at a word boundary. Only the parts that can end up in the tail are joined.
    /// </summary>
    private static string JoinTail(List<string> parts, int maxChars, out bool truncated)
    {
        truncated = false;
        if (maxChars <= 0 || parts.Count == 0)
        {
            truncated = parts.Count > 0;
            return "";
        }

        var first = parts.Count;
        var length = -1;
        while (first > 0 && length <= maxChars)
        {
            first--;
            length += parts[first].Length + 1;
        }

        var builder = new StringBuilder(length);
        for (var i = first; i < parts.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(parts[i]);
        }

        if (builder.Length <= maxChars)
        {
            return builder.ToString();
        }

        truncated = true;
        var joined = builder.ToString();
        var start = joined.Length - maxChars;
        if (!char.IsWhiteSpace(joined[start - 1]) && !char.IsWhiteSpace(joined[start]))
        {
            // Mid-word: skip to the next word (unless the tail is a single long word).
            for (var i = start; i < joined.Length; i++)
            {
                if (char.IsWhiteSpace(joined[i]))
                {
                    start = i + 1;
                    break;
                }
            }
        }

        return joined[start..].TrimStart();
    }
}
