using InterviewAssist.Core.Models;
using InterviewAssist.Core.Transcription;

namespace InterviewAssist.Core.Tests;

public class TranscriptStoreTests
{
    private static TranscriptResult Final(string text, double start, double end, int stream = 1) =>
        new(stream, text, IsFinal: true, SpeechFinal: false, FromFinalize: false, start, end - start);

    private static TranscriptResult Interim(string text, double start, double end, int stream = 1) =>
        new(stream, text, IsFinal: false, SpeechFinal: false, FromFinalize: false, start, end - start);

    [Fact]
    public void NewStore_IsEmpty()
    {
        var store = new TranscriptStore();

        Assert.Empty(store.GetSegments());
        Assert.Equal("", store.GetPendingText());
        Assert.Equal("", store.GetRecentContext(100));
        Assert.Equal(new TranscriptSnapshot("", "", ""), store.GetSnapshot());
        Assert.Equal("", store.TakePendingQuestion());
    }

    [Fact]
    public void Interim_IsReplacedByNewerInterim()
    {
        var store = new TranscriptStore();

        store.Apply(Interim("tell", 0, 0.4));
        store.Apply(Interim("tell me about", 0, 1.2));

        var snapshot = store.GetSnapshot();
        Assert.Equal("", snapshot.History);
        Assert.Equal("", snapshot.Pending);
        Assert.Equal("tell me about", snapshot.Interim);
        Assert.True(snapshot.HasPending);
        Assert.Empty(store.GetSegments());
    }

    [Fact]
    public void Interim_IsReplacedByFinal()
    {
        var store = new TranscriptStore();

        store.Apply(Interim("tell me abou", 0, 1.1));
        store.Apply(Final("Tell me about yourself.", 0, 1.4));

        var snapshot = store.GetSnapshot();
        Assert.Equal("Tell me about yourself.", snapshot.Pending);
        Assert.Equal("", snapshot.Interim);
        var segment = Assert.Single(store.GetSegments());
        Assert.Equal(1, segment.StreamId);
        Assert.Equal(0, segment.Start);
        Assert.Equal(1.4, segment.End, 3);
        Assert.Equal("Tell me about yourself.", segment.Text);
    }

    [Fact]
    public void Final_KeepsInterimForLaterAudio()
    {
        var store = new TranscriptStore();

        store.Apply(Interim("and what", 1.2, 2.0));
        store.Apply(Final("First part.", 0, 1.0));

        var snapshot = store.GetSnapshot();
        Assert.Equal("First part.", snapshot.Pending);
        Assert.Equal(" and what", snapshot.Interim);
        Assert.Equal("First part. and what", store.GetPendingText());
    }

    [Fact]
    public void Final_FromAnotherStream_DoesNotClearInterim()
    {
        var store = new TranscriptStore();

        store.Apply(Interim("still talking", 0.5, 1.5, stream: 2));
        store.Apply(Final("late words", 9.0, 10.0, stream: 1));

        Assert.Equal(" still talking", store.GetSnapshot().Interim);
    }

    [Fact]
    public void WhitespaceOnlyText_IsIgnored_AndDoesNotRaiseChanged()
    {
        var store = new TranscriptStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Apply(Final("   ", 0, 1));
        store.Apply(Interim("\t\n", 0, 1));
        store.Apply(Final("", 0, 1));

        Assert.Equal(0, changes);
        Assert.Empty(store.GetSegments());
        Assert.False(store.GetSnapshot().HasPending);
    }

    [Fact]
    public void Apply_TrimsSegmentText()
    {
        var store = new TranscriptStore();

        store.Apply(Final("  Hello there.  ", 0, 1));

        Assert.Equal("Hello there.", Assert.Single(store.GetSegments()).Text);
    }

    [Fact]
    public void TakePendingQuestion_IncludesInterim()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Tell me", 0, 0.8));
        store.Apply(Interim("about yourself", 0.9, 1.8));

        var question = store.TakePendingQuestion();

        Assert.Equal("Tell me about yourself", question);
        Assert.Equal("", store.GetPendingText());
        var snapshot = store.GetSnapshot();
        Assert.False(snapshot.HasPending);
        Assert.Equal("Tell me about yourself", snapshot.History);
    }

    [Fact]
    public void TakePendingQuestion_NormalizesWhitespace()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Why   do you\twant", 0, 1));
        store.Apply(Final("this   job?", 1, 2));

        Assert.Equal("Why do you want this job?", store.TakePendingQuestion());
    }

    [Fact]
    public void FinalArrivingAfterTake_ForTheSameAudio_IsNotPending()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Tell me", 0, 0.8));
        store.Apply(Interim("about yourself", 0.9, 1.8));
        store.TakePendingQuestion();

        store.Apply(Final("about yourself.", 0.9, 1.9));

        Assert.Equal("", store.GetPendingText());
        var snapshot = store.GetSnapshot();
        Assert.Equal("Tell me about yourself.", snapshot.History);
        Assert.Equal("", snapshot.Pending);
        Assert.Equal("", snapshot.Interim);
        Assert.Equal("", store.TakePendingQuestion());
    }

    [Fact]
    public void NextQuestion_OnlyContainsNewSpeech()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Tell me", 0, 0.8));
        store.Apply(Interim("about yourself", 0.9, 1.8));
        Assert.Equal("Tell me about yourself", store.TakePendingQuestion());
        store.Apply(Final("about yourself.", 0.9, 1.9));

        store.Apply(Final("What is your biggest", 2.5, 3.6));
        store.Apply(Interim("weakness", 3.7, 4.1));

        Assert.Equal("What is your biggest weakness", store.TakePendingQuestion());

        store.Apply(Final("weakness?", 3.7, 4.2));
        store.Apply(Final("Take your time.", 5.0, 6.0));

        Assert.Equal("Take your time.", store.TakePendingQuestion());
    }

    [Fact]
    public void PendingRule_AllowsSmallTimestampJitterBeforeTheMarker()
    {
        var store = new TranscriptStore();
        store.Apply(Final("First question?", 0, 1.8));
        store.TakePendingQuestion(); // marker = (1, 1.8)

        store.Apply(Final("too early", 1.70, 1.9));   // before marker - 0.05
        store.Apply(Final("within tolerance", 1.76, 2.5));

        Assert.Equal("within tolerance", store.GetPendingText());
    }

    [Fact]
    public void NewStreamAfterReconnect_IsPending()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Question on the first connection?", 0, 10));
        store.TakePendingQuestion(); // marker = (1, 10)

        store.Apply(Final("After the reconnect", 0.2, 1.5, stream: 2));

        Assert.Equal("After the reconnect", store.GetPendingText());
        Assert.Equal("After the reconnect", store.TakePendingQuestion());
        Assert.Equal("", store.GetPendingText());
    }

    [Fact]
    public void InterimFromOlderStream_IsIgnored()
    {
        var store = new TranscriptStore();
        store.Apply(Final("New stream words", 0, 1, stream: 2));
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Apply(Interim("old stream hypothesis", 20, 21, stream: 1));

        Assert.Equal(0, changes);
        Assert.Equal("", store.GetSnapshot().Interim);
        Assert.Equal("New stream words", store.GetPendingText());
    }

    [Fact]
    public void UnfinalizedInterim_FromPreviousStream_IsKeptWhenANewStreamStarts()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Tell me about", 4, 5));
        store.Apply(Interim("a time when", 5.1, 6));

        store.Apply(Final("you failed.", 0.1, 1, stream: 2));

        var segments = store.GetSegments();
        Assert.Equal(3, segments.Count);
        Assert.Equal(1, segments[1].StreamId);
        Assert.Equal("a time when", segments[1].Text);
        Assert.Equal("", store.GetSnapshot().Interim);
        Assert.Equal("Tell me about a time when you failed.", store.TakePendingQuestion());
    }

    [Fact]
    public void Snapshot_SplitsHistoryPendingAndInterim()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Hello, thanks for joining.", 0, 1.5));
        store.Apply(Final("Tell me about yourself.", 1.6, 3));
        store.TakePendingQuestion();

        store.Apply(Final("And why this company?", 5, 6.5));
        store.Apply(Interim("Take your", 6.6, 7));

        var snapshot = store.GetSnapshot();
        Assert.Equal("Hello, thanks for joining. Tell me about yourself.", snapshot.History);
        Assert.Equal(" And why this company?", snapshot.Pending);
        Assert.Equal(" Take your", snapshot.Interim);
        Assert.True(snapshot.HasPending);
        Assert.Equal(
            "Hello, thanks for joining. Tell me about yourself. And why this company? Take your",
            snapshot.History + snapshot.Pending + snapshot.Interim);
    }

    [Fact]
    public void Snapshot_WithOnlyPendingText_HasNoLeadingSpace()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Hi there.", 0, 1));
        store.Apply(Interim("How are", 1.1, 1.5));

        var snapshot = store.GetSnapshot();
        Assert.Equal("", snapshot.History);
        Assert.Equal("Hi there.", snapshot.Pending);
        Assert.Equal(" How are", snapshot.Interim);
    }

    [Fact]
    public void Snapshot_ShowsNonPendingInterimInHistory()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Tell me", 0, 0.8));
        store.Apply(Interim("about yourself", 0.9, 1.8));
        store.TakePendingQuestion();

        var snapshot = store.GetSnapshot();

        Assert.Equal("Tell me about yourself", snapshot.History);
        Assert.Equal("", snapshot.Pending);
        Assert.Equal("", snapshot.Interim);
    }

    [Fact]
    public void Snapshot_TruncatesHistoryAtWordBoundary_WithEllipsis()
    {
        var store = new TranscriptStore();
        store.Apply(Final("alpha beta gamma", 0, 1));
        store.Apply(Final("delta epsilon", 1, 2));
        store.TakePendingQuestion();

        Assert.Equal("alpha beta gamma delta epsilon", store.GetSnapshot(100).History);
        Assert.Equal("alpha beta gamma delta epsilon", store.GetSnapshot(30).History);
        Assert.Equal("… delta epsilon", store.GetSnapshot(13).History); // cut lands on a word start
        Assert.Equal("… epsilon", store.GetSnapshot(12).History);       // cut mid-word -> next word
        Assert.Equal("", store.GetSnapshot(0).History);
    }

    [Fact]
    public void GetRecentContext_ReturnsTailOfFinalsBeforeTheMarker()
    {
        var store = new TranscriptStore();
        store.Apply(Final("one two three", 0, 1));
        store.Apply(Final("four five", 1, 2));
        store.TakePendingQuestion();
        store.Apply(Final("six", 3, 4));
        store.Apply(Interim("seven", 4.1, 4.5));

        Assert.Equal("one two three four five", store.GetRecentContext(100));
        Assert.Equal("four five", store.GetRecentContext(9));
        Assert.Equal("five", store.GetRecentContext(7));
        Assert.Equal("", store.GetRecentContext(0));
    }

    [Fact]
    public void GetRecentContext_IsEmptyWhenEverythingIsPending()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Nothing taken yet.", 0, 1));

        Assert.Equal("", store.GetRecentContext(500));
    }

    [Fact]
    public void Clear_DropsEverything_AndOldAudioIsNotCountedAgain()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Tell me", 0, 0.8));
        store.Apply(Interim("about yourself", 0.9, 1.8));
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Clear();

        Assert.Equal(1, changes);
        Assert.Empty(store.GetSegments());
        Assert.Equal(new TranscriptSnapshot("", "", ""), store.GetSnapshot());
        Assert.Equal("", store.GetPendingText());

        // Taking with nothing new must not move the marker back to 0.
        Assert.Equal("", store.TakePendingQuestion());
        store.Apply(Final("about yourself.", 0.9, 1.9));
        Assert.Equal("", store.GetPendingText());

        store.Apply(Final("Next question?", 3, 4));
        Assert.Equal("Next question?", store.TakePendingQuestion());
    }

    [Fact]
    public void Clear_ThenNewStream_IsPending()
    {
        var store = new TranscriptStore();
        store.Apply(Final("Old talk", 0, 30));
        store.Clear();

        store.Apply(Final("Fresh start", 0, 1, stream: 2));

        Assert.Equal("Fresh start", store.GetPendingText());
    }

    [Fact]
    public void Changed_IsRaisedAfterEveryMutation()
    {
        var store = new TranscriptStore();
        var changes = 0;
        store.Changed += (_, _) =>
        {
            // The lock must not be held while raising: reading from the handler would deadlock otherwise.
            _ = store.GetSnapshot();
            changes++;
        };

        store.Apply(Interim("hi", 0, 0.5));
        store.Apply(Final("Hi.", 0, 0.6));
        store.TakePendingQuestion();
        store.Clear();

        Assert.Equal(4, changes);
    }

    [Fact]
    public void Segments_AreCappedAt2000_DroppingTheOldest()
    {
        var store = new TranscriptStore();
        for (var i = 0; i < 2005; i++)
        {
            store.Apply(Final($"word{i}", i, i + 0.5));
        }

        var segments = store.GetSegments();
        Assert.Equal(2000, segments.Count);
        Assert.Equal("word5", segments[0].Text);
        Assert.Equal("word2004", segments[^1].Text);
    }

    [Fact]
    public async Task ConcurrentApplyAndTake_AreThreadSafe()
    {
        var store = new TranscriptStore();
        var taken = new System.Collections.Concurrent.ConcurrentBag<string>();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                store.Apply(Interim($"w{i}", i, i + 0.4));
                store.Apply(Final($"w{i}", i, i + 0.5));
            }
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                taken.Add(store.TakePendingQuestion());
                _ = store.GetSnapshot(200);
                _ = store.GetRecentContext(200);
            }
        });

        await Task.WhenAll(writer, reader);
        taken.Add(store.TakePendingQuestion());

        Assert.Equal(1000, store.GetSegments().Count);
        Assert.Equal("", store.GetPendingText());

        // Every word ends up in exactly one question: either via its interim (then the later final for the
        // same audio is not pending) or via its final.
        var counts = taken
            .SelectMany(q => q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(w => w)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1000, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }
}
