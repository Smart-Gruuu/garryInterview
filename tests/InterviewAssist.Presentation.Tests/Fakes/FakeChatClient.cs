using System.Runtime.CompilerServices;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Presentation.Tests.Fakes;

/// <summary>
/// One scripted answer stream: the deltas are yielded synchronously, then the stream ends, throws
/// <see cref="Error"/>, or (with <see cref="WaitForCancellation"/>) stays open until its token is cancelled.
/// </summary>
public sealed record FakeChatReply(IReadOnlyList<string> Deltas, Exception? Error = null, bool WaitForCancellation = false)
{
    public static FakeChatReply Text(params string[] deltas) => new(deltas);

    public static FakeChatReply Hang(params string[] deltas) => new(deltas, WaitForCancellation: true);

    public static FakeChatReply Fail(Exception error, params string[] deltas) => new(deltas, error);
}

public sealed class FakeChatClient : IChatCompletionClient
{
    /// <summary>Replies for the next streams, in order; <see cref="DefaultReply"/> once empty.</summary>
    public Queue<FakeChatReply> Replies { get; } = new();

    public FakeChatReply DefaultReply { get; set; } = FakeChatReply.Text("An answer.");

    public List<ChatRequest> Requests { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public IReadOnlyList<string> Models { get; set; } = [];

    public Exception? ListModelsException { get; set; }

    public List<(string BaseUrl, string ApiKey)> ModelListRequests { get; } = [];

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(request);
        Tokens.Add(ct);
        var reply = Replies.Count > 0 ? Replies.Dequeue() : DefaultReply;

        foreach (var delta in reply.Deltas)
        {
            ct.ThrowIfCancellationRequested();
            yield return delta;
        }

        if (reply.Error is not null)
        {
            throw reply.Error;
        }

        if (reply.WaitForCancellation)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetCanceled(ct)))
            {
                await cancelled.Task;
            }
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        ModelListRequests.Add((baseUrl, apiKey));
        return ListModelsException is null
            ? Task.FromResult(Models)
            : Task.FromException<IReadOnlyList<string>>(ListModelsException);
    }
}
