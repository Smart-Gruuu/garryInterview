using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Abstractions;

/// <summary>Streaming speech-to-text (Deepgram live WebSocket API).</summary>
public interface ITranscriptionClient : IAsyncDisposable
{
    TranscriptionState State { get; }

    /// <summary>
    /// Opens the stream. Completes when connected. Throws <see cref="InvalidOperationException"/> with a
    /// user-facing message when the connection is rejected (e.g. invalid API key). Once started, unexpected
    /// disconnects are retried automatically (State = Reconnecting) until <see cref="StopAsync"/> is called.
    /// </summary>
    Task StartAsync(TranscriptionOptions options, CancellationToken ct = default);

    /// <summary>Queues PCM16 16 kHz mono audio. Thread-safe and non-blocking. Buffered briefly while reconnecting.</summary>
    void SendAudio(ReadOnlyMemory<byte> pcm16);

    /// <summary>
    /// Asks the server to finalize everything received so far. Returns true once the finalize response has
    /// arrived (its results already raised via <see cref="ResultReceived"/>), false on timeout or when not connected.
    /// </summary>
    Task<bool> FinalizeAsync(TimeSpan timeout);

    /// <summary>Flushes and closes the stream gracefully. Safe to call when not started.</summary>
    Task StopAsync();

    /// <summary>Validates an API key without opening a stream. Returns (ok, user-facing message).</summary>
    Task<(bool Ok, string Message)> TestApiKeyAsync(string apiKey, CancellationToken ct = default);

    /// <summary>Non-empty transcript results, interim and final. Background thread.</summary>
    event EventHandler<TranscriptResult>? ResultReceived;

    /// <summary>Server detected the end of an utterance (a pause after speech). Background thread.</summary>
    event EventHandler? UtteranceEnd;

    event EventHandler<TranscriptionState>? StateChanged;

    /// <summary>User-facing error description (auth failure, reconnect gave up, server error). Background thread.</summary>
    event EventHandler<string>? Error;
}
