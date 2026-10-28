using System.Text;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Transcription;

/// <summary>
/// The editable question box. New final captions are appended to <see cref="Text"/> as they arrive — including while
/// the user is editing it (e.g. deleting small talk before asking). Segments are tracked by sequence number, so text
/// the user deleted is never re-added. Not thread-safe: use from the UI thread.
/// </summary>
public sealed class QuestionDraft
{
    private string _text = "";

    /// <summary>The question as the user sees and edits it.</summary>
    public string Text
    {
        get => _text;
        set => _text = value ?? "";
    }

    /// <summary>Sequence of the newest segment already appended to <see cref="Text"/> (or consumed by <see cref="Take"/>).</summary>
    public long SyncedSequence { get; private set; }

    /// <summary>Appends finals newer than <see cref="SyncedSequence"/> to <see cref="Text"/>. Returns true when Text changed.</summary>
    public bool Sync(PendingTranscript pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var fresh = NewText(pending.Finals, out var lastSequence);
        SyncedSequence = lastSequence;
        if (fresh.Length == 0)
            return false;

        Text = Append(Text, fresh);
        return true;
    }

    /// <summary>
    /// Returns the question to send — the edited text, then any finals not appended yet, then the interim — and empties
    /// the draft. Everything in <paramref name="taken"/> counts as used.
    /// </summary>
    public string Take(PendingTranscript taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var fresh = NewText(taken.Finals, out var lastSequence);
        var question = Join(Join(Text.Trim(), fresh), taken.Interim.Trim());

        SyncedSequence = lastSequence;
        Text = "";
        return question;
    }

    /// <summary>Empties the draft. Segments already seen are not brought back.</summary>
    public void Clear() => Text = "";

    private string NewText(IReadOnlyList<TranscriptSegment> finals, out long lastSequence)
    {
        lastSequence = SyncedSequence;
        var builder = new StringBuilder();
        foreach (var segment in finals)
        {
            if (segment.Sequence <= SyncedSequence)
                continue;

            var text = segment.Text.Trim();
            if (text.Length > 0)
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(text);
            }
            lastSequence = Math.Max(lastSequence, segment.Sequence);
        }
        return builder.ToString();
    }

    /// <summary>Appends <paramref name="addition"/> after <paramref name="text"/>, keeping the user's own trailing whitespace.</summary>
    private static string Append(string text, string addition)
    {
        if (text.Length == 0)
            return addition;
        return char.IsWhiteSpace(text[^1]) ? text + addition : text + " " + addition;
    }

    private static string Join(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : first + " " + second;
}
