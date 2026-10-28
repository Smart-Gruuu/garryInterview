using InterviewAssist.Core.Models;
using InterviewAssist.Core.Transcription;

namespace InterviewAssist.Core.Tests;

public class QuestionDraftTests
{
    private readonly TranscriptStore _store = new();
    private readonly QuestionDraft _draft = new();

    private void Final(string text, double start, double end, int stream = 1) =>
        _store.Apply(new TranscriptResult(stream, text, IsFinal: true, SpeechFinal: false, FromFinalize: false, start, end - start));

    private void Interim(string text, double start, double end, int stream = 1) =>
        _store.Apply(new TranscriptResult(stream, text, IsFinal: false, SpeechFinal: false, FromFinalize: false, start, end - start));

    private bool Sync() => _draft.Sync(_store.GetPending());

    private string Take() => _draft.Take(_store.TakePending());

    // ---- store: sequences and pending ----

    [Fact]
    public void Store_assigns_increasing_sequences_that_survive_clear()
    {
        Final("one", 0, 1);
        Final("two", 1, 2);
        var before = _store.GetSegments().Select(s => s.Sequence).ToArray();
        _store.Clear();
        Final("three", 3, 4);

        Assert.Equal([1L, 2L], before);
        Assert.Equal(3L, _store.GetSegments().Single().Sequence);
    }

    [Fact]
    public void Store_pending_has_finals_and_interim_and_take_consumes_them()
    {
        Final("So tell me", 0, 1);
        Interim("about yourself", 1, 2);

        var pending = _store.GetPending();
        Assert.Equal(["So tell me"], pending.Finals.Select(s => s.Text));
        Assert.Equal("about yourself", pending.Interim);

        var taken = _store.TakePending();
        Assert.Equal(pending.Finals, taken.Finals);
        Assert.Equal(pending.Interim, taken.Interim);
        Assert.Same(PendingTranscript.Empty, _store.GetPending());

        // The final for the interim that was already sent is not pending again.
        Final("about yourself.", 1, 2.1);
        Assert.Same(PendingTranscript.Empty, _store.GetPending());
    }

    // ---- draft ----

    [Fact]
    public void Finals_are_appended_to_the_draft_interim_is_not()
    {
        Final("So, um,", 0, 1);
        Final("tell me about yourself.", 1, 3);
        Interim("And also", 3, 4);

        Assert.True(Sync());

        Assert.Equal("So, um, tell me about yourself.", _draft.Text);
        Assert.False(Sync()); // nothing new
    }

    [Fact]
    public void User_edits_are_kept_and_deleted_text_is_not_re_added()
    {
        Final("Nice weather today.", 0, 2);
        Final("Why do you want this job?", 2, 4);
        Sync();

        _draft.Text = "Why do you want this job?"; // user deleted the small talk
        Final("And why now?", 4, 5);
        Sync();

        Assert.Equal("Why do you want this job? And why now?", _draft.Text);
    }

    [Fact]
    public void Appending_respects_the_users_trailing_whitespace()
    {
        Final("First part.", 0, 1);
        Sync();
        _draft.Text = "First part.\n";
        Final("Second part.", 1, 2);
        Sync();

        Assert.Equal("First part.\nSecond part.", _draft.Text);
    }

    [Fact]
    public void Captions_keep_appending_while_the_user_edits()
    {
        Final("Um, so, tell me about", 0, 1);
        Sync();
        _draft.Text = "Tell me about"; // user trimmed the start mid-question

        Final("a time you failed.", 1, 3);
        Assert.True(Sync());
        Assert.Equal("Tell me about a time you failed.", _draft.Text);

        _draft.Text = "Tell me about a time you failed"; // still editing
        Final("And what you learned.", 3, 4);
        Assert.True(Sync());
        Assert.Equal("Tell me about a time you failed And what you learned.", _draft.Text);
    }

    [Fact]
    public void Take_sends_the_edited_text_plus_unsynced_finals_plus_interim_then_empties()
    {
        Final("Blah blah.", 0, 1);
        Final("What's your biggest weakness?", 1, 3);
        Sync();
        _draft.Text = "  What's your biggest weakness?  ";
        Final("Be honest.", 3, 4); // arrived after the last refresh (e.g. flushed by Finalize on Enter)
        Interim("haha", 4, 5);

        var question = Take();

        Assert.Equal("What's your biggest weakness? Be honest. haha", question);
        Assert.Equal("", _draft.Text);
    }

    [Fact]
    public void Next_question_starts_fresh_after_take()
    {
        Final("First question?", 0, 2);
        Sync();
        Assert.Equal("First question?", Take());

        Final("Second question?", 5, 7);
        Sync();

        Assert.Equal("Second question?", _draft.Text);
        Assert.Equal("Second question?", Take());
    }

    [Fact]
    public void Take_with_everything_deleted_and_nothing_new_is_empty()
    {
        Final("Um okay.", 0, 1);
        Sync();
        _draft.Text = "   ";

        Assert.Equal("", Take());
    }

    [Fact]
    public void Typed_text_without_speech_is_sent()
    {
        _draft.Text = "How do you handle deadlines?";

        Assert.Equal("How do you handle deadlines?", Take());
    }

    [Fact]
    public void Clear_empties_the_draft_and_cleared_speech_never_comes_back()
    {
        Final("Old stuff.", 0, 1);
        Sync();
        Final("More old stuff.", 1, 2); // not synced yet when Clear is pressed

        _draft.Clear();
        _store.Clear();
        Sync();
        Assert.Equal("", _draft.Text);

        Final("New question?", 3, 4);
        Sync();
        Assert.Equal("New question?", _draft.Text);
    }

    [Fact]
    public void Reconnect_stream_finals_are_appended()
    {
        Final("Before the drop.", 0, 1, stream: 1);
        Sync();
        Final("After the reconnect.", 0, 1, stream: 2);
        Sync();

        Assert.Equal("Before the drop. After the reconnect.", _draft.Text);
    }
}
