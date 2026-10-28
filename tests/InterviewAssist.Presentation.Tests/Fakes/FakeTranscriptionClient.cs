using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Presentation.Tests.Fakes;

public sealed class FakeTranscriptionClient : ITranscriptionClient
{
    private readonly object _audioGate = new();
    private readonly List<byte[]> _sentAudio = [];

    public TranscriptionState State { get; set; } = TranscriptionState.Disconnected;

    /// <summary>When set, <see cref="StartAsync"/> fails with it.</summary>
    public Exception? StartException { get; set; }

    public List<TranscriptionOptions> Starts { get; } = [];

    public int StopCount { get; private set; }

    public int FinalizeCount { get; private set; }

    /// <summary>What <see cref="FinalizeAsync"/> does, e.g. deliver the last words or stay pending. Default: true.</summary>
    public Func<Task<bool>>? OnFinalize { get; set; }

    public (bool Ok, string Message) TestResult { get; set; } = (true, "Key works.");

    public Exception? TestException { get; set; }

    public List<string> TestedKeys { get; } = [];

    public IReadOnlyList<byte[]> SentAudio
    {
        get
        {
            lock (_audioGate)
            {
                return [.. _sentAudio];
            }
        }
    }

    public event EventHandler<TranscriptResult>? ResultReceived;

    // The view models never subscribe to it.
    public event EventHandler? UtteranceEnd
    {
        add { }
        remove { }
    }

    public event EventHandler<TranscriptionState>? StateChanged;

    public event EventHandler<string>? Error;

    public Task StartAsync(TranscriptionOptions options, CancellationToken ct = default)
    {
        Starts.Add(options);
        if (StartException is not null)
        {
            return Task.FromException(StartException);
        }

        State = TranscriptionState.Connected;
        return Task.CompletedTask;
    }

    public void SendAudio(ReadOnlyMemory<byte> pcm16)
    {
        lock (_audioGate)
        {
            _sentAudio.Add(pcm16.ToArray());
        }
    }

    public Task<bool> FinalizeAsync(TimeSpan timeout)
    {
        FinalizeCount++;
        return OnFinalize?.Invoke() ?? Task.FromResult(true);
    }

    public Task StopAsync()
    {
        StopCount++;
        State = TranscriptionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<(bool Ok, string Message)> TestApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        TestedKeys.Add(apiKey);
        return TestException is null ? Task.FromResult(TestResult) : Task.FromException<(bool, string)>(TestException);
    }

    public void RaiseResult(TranscriptResult result) => ResultReceived?.Invoke(this, result);

    public void RaiseState(TranscriptionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void RaiseError(string message) => Error?.Invoke(this, message);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
