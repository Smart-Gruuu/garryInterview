using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Abstractions;

/// <summary>OpenAI-compatible Chat Completions client (streaming).</summary>
public interface IChatCompletionClient
{
    /// <summary>
    /// Streams the assistant's reply as text deltas. Throws <see cref="ChatClientException"/> with a user-facing
    /// message on HTTP/API errors; throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct = default);

    /// <summary>Model ids available to this key, sorted. Throws <see cref="ChatClientException"/> on failure.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken ct = default);
}
