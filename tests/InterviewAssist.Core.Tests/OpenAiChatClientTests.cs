using System.Net;
using System.Text;
using System.Text.Json;
using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Tests;

public class OpenAiChatClientTests
{
    private const string OpenAiBase = "https://api.openai.com/v1";

    private static ChatRequest Request(
        string baseUrl = OpenAiBase,
        double? temperature = 0.4,
        string? reasoningEffort = null,
        string model = "gpt-4.1-mini",
        int maxTokens = 700) =>
        new(baseUrl, "sk-test", model,
            [new ChatMessage("system", "You are a copilot."), new ChatMessage("user", "Interviewer's question: Why us?")],
            maxTokens, temperature, reasoningEffort);

    private static async Task<List<string>> CollectAsync(OpenAiChatClient client, ChatRequest request, CancellationToken ct = default)
    {
        var deltas = new List<string>();
        await foreach (var delta in client.StreamAsync(request, ct))
            deltas.Add(delta);
        return deltas;
    }

    private static (OpenAiChatClient Client, FakeHandler Handler) Create(Func<HttpResponseMessage> respond)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(respond()));
        return (new OpenAiChatClient(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Sse(string body, int chunkSize = 7, bool hangAtEnd = false) =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(ChunkedStream.Split(Encoding.UTF8.GetBytes(body), chunkSize, hangAtEnd)),
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---- streaming ----

    [Fact]
    public async Task Streams_deltas_split_across_reads()
    {
        const string body =
            ": keep-alive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"**I led** \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"the café rewrite — 40% faster.\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var (client, _) = Create(() => Sse(body, chunkSize: 5));

        var deltas = await CollectAsync(client, Request());

        Assert.Equal(["**I led** ", "the café rewrite — 40% faster."], deltas);
    }

    [Fact]
    public async Task Handles_crlf_line_endings()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\r\n\r\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\r\n\r\n" +
            "data: [DONE]\r\n\r\n";
        var (client, _) = Create(() => Sse(body, chunkSize: 3));

        Assert.Equal(["A", "B"], await CollectAsync(client, Request()));
    }

    [Fact]
    public async Task Stops_at_done_without_waiting_for_the_connection_to_close()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"after done\"}}]}\n\n";
        var (client, _) = Create(() => Sse(body, hangAtEnd: true));

        var deltas = await CollectAsync(client, Request()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Hi"], deltas);
    }

    [Fact]
    public async Task Completes_when_the_stream_ends_without_done()
    {
        var (client, _) = Create(() => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Partial\"}}]}\n"));

        Assert.Equal(["Partial"], await CollectAsync(client, Request()));
    }

    [Fact]
    public async Task Error_event_in_stream_throws_after_earlier_deltas()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Start\"}}]}\n\n" +
            "data: {\"error\":{\"message\":\"The server had an error while processing your request.\"}}\n\n";
        var (client, _) = Create(() => Sse(body));
        var deltas = new List<string>();

        var ex = await Assert.ThrowsAsync<ChatClientException>(async () =>
        {
            await foreach (var delta in client.StreamAsync(Request()))
                deltas.Add(delta);
        });

        Assert.Equal(["Start"], deltas);
        Assert.Equal("OpenAI error: The server had an error while processing your request.", ex.Message);
    }

    [Fact]
    public async Task Stream_with_no_text_throws_a_friendly_error()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
            "data: [DONE]\n\n";
        var (client, _) = Create(() => Sse(body));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.StartsWith("The model returned an empty answer.", ex.Message);
    }

    [Fact]
    public async Task Non_streamed_json_body_is_still_returned()
    {
        const string body =
            """
            {
              "id": "chatcmpl-1",
              "object": "chat.completion",
              "choices": [ { "index": 0, "message": { "role": "assistant", "content": "Whole answer" }, "finish_reason": "stop" } ]
            }
            """;
        var (client, _) = Create(() => Json(HttpStatusCode.OK, body));

        Assert.Equal(["Whole answer"], await CollectAsync(client, Request()));
    }

    [Fact]
    public async Task Json_error_body_with_success_status_throws()
    {
        var (client, _) = Create(() => Json(HttpStatusCode.OK, """{"error":{"message":"Model is loading"}}"""));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.Equal("OpenAI error: Model is loading", ex.Message);
    }

    // ---- HTTP errors ----

    [Theory]
    [InlineData(401, """{"error":{"message":"Incorrect API key provided: sk-test.","type":"invalid_request_error"}}""",
        "OpenAI rejected the API key (401). Check it in Settings.")]
    [InlineData(404, """{"error":{"message":"The model `gpt-9` does not exist","code":"model_not_found"}}""",
        "Model 'gpt-4.1-mini' was not found or you don't have access (404).")]
    [InlineData(429, """{"error":{"message":"You exceeded your current quota.","type":"insufficient_quota"}}""",
        "OpenAI rate limit or quota exceeded (429): You exceeded your current quota.")]
    [InlineData(500, """{"error":{"message":"The server had an error processing your request."}}""",
        "OpenAI error 500: The server had an error processing your request.")]
    [InlineData(400, """{"error":{"message":"Unsupported parameter: 'temperature'."}}""",
        "OpenAI error 400: Unsupported parameter: 'temperature'.")]
    public async Task Http_errors_map_to_friendly_messages(int status, string body, string expected)
    {
        var (client, _) = Create(() => Json((HttpStatusCode)status, body));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.Equal(expected, ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task Html_error_page_falls_back_to_the_reason_phrase()
    {
        var (client, _) = Create(() => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
        });

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.Equal("OpenAI error 502: Bad Gateway", ex.Message);
    }

    [Fact]
    public async Task Plain_text_error_body_is_shown()
    {
        var (client, _) = Create(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("upstream overloaded"),
        });

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.Equal("OpenAI error 503: upstream overloaded", ex.Message);
    }

    [Fact]
    public async Task Network_failure_throws_could_not_reach()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No such host is known. (api.openai.com:443)"));
        var client = new OpenAiChatClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.Equal("Could not reach OpenAI: No such host is known. (api.openai.com:443)", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Slow_response_headers_time_out_with_a_friendly_error()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new OpenAiChatClient(new HttpClient(handler)) { HeadersTimeout = TimeSpan.FromMilliseconds(100) };

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request()));

        Assert.StartsWith("Could not reach OpenAI: no response within", ex.Message);
    }

    [Fact]
    public async Task Invalid_base_url_throws_a_friendly_error()
    {
        var (client, handler) = Create(() => Sse("data: [DONE]\n"));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => CollectAsync(client, Request(baseUrl: "not a url")));

        Assert.Contains("base URL", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Api_key_with_inner_whitespace_is_rejected_before_sending()
    {
        var (client, handler) = Create(() => Sse("data: [DONE]\n"));

        var ex = await Assert.ThrowsAsync<ChatClientException>(
            () => CollectAsync(client, Request() with { ApiKey = "sk-te st" }));

        Assert.Contains("API key contains invalid characters", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancelled_token_before_start_throws_operation_cancelled()
    {
        var (client, handler) = Create(() => Sse("data: [DONE]\n"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(client, Request(), cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_headers_throws_operation_cancelled()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new OpenAiChatClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(client, Request(), cts.Token));
    }

    [Fact]
    public async Task Cancellation_mid_stream_throws_operation_cancelled_and_keeps_received_text()
    {
        var (client, _) = Create(() => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"First\"}}]}\n\n", hangAtEnd: true));
        using var cts = new CancellationTokenSource();
        var deltas = new List<string>();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var delta in client.StreamAsync(Request(), cts.Token))
            {
                deltas.Add(delta);
                cts.Cancel();
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(["First"], deltas);
    }

    // ---- request ----

    [Fact]
    public async Task Request_targets_chat_completions_with_auth_and_streaming_body()
    {
        var (client, handler) = Create(() => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n"));

        await CollectAsync(client, Request(baseUrl: OpenAiBase + "/"));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", sent.Uri.ToString());
        Assert.Equal("Bearer sk-test", sent.Authorization);
        Assert.Contains("text/event-stream", sent.Accept);
        Assert.StartsWith("application/json", sent.ContentType);

        using var doc = JsonDocument.Parse(sent.Body!);
        var root = doc.RootElement;
        Assert.Equal("gpt-4.1-mini", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are a copilot.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Interviewer's question: Why us?", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAi_host_uses_max_completion_tokens()
    {
        using var doc = JsonDocument.Parse(OpenAiChatClient.BuildRequestBody(Request(maxTokens: 900)));

        Assert.Equal(900, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://myresource.openai.azure.com/openai/v1")]
    public void Other_hosts_use_max_tokens(string baseUrl)
    {
        using var doc = JsonDocument.Parse(OpenAiChatClient.BuildRequestBody(Request(baseUrl: baseUrl, maxTokens: 900)));

        Assert.Equal(900, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public void Temperature_is_sent_when_set_and_omitted_when_null()
    {
        using var withTemp = JsonDocument.Parse(OpenAiChatClient.BuildRequestBody(Request(temperature: 0.4)));
        using var withoutTemp = JsonDocument.Parse(OpenAiChatClient.BuildRequestBody(Request(temperature: null)));

        Assert.Equal(0.4, withTemp.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(withoutTemp.RootElement.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("low", true)]
    [InlineData("high", true)]
    public void Reasoning_effort_is_sent_only_when_set(string? effort, bool expected)
    {
        using var doc = JsonDocument.Parse(OpenAiChatClient.BuildRequestBody(Request(reasoningEffort: effort, temperature: null)));

        Assert.Equal(expected, doc.RootElement.TryGetProperty("reasoning_effort", out var value));
        if (expected)
            Assert.Equal(effort, value.GetString());
    }

    [Fact]
    public void Request_body_escapes_json_correctly()
    {
        var request = new ChatRequest(OpenAiBase, "k", "gpt-4o",
            [new ChatMessage("user", "Line \"one\"\nñ 日本 <tag> & \\ end")], 100, null, null);

        using var doc = JsonDocument.Parse(OpenAiChatClient.BuildRequestBody(request));

        Assert.Equal("Line \"one\"\nñ 日本 <tag> & \\ end",
            doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    // ---- models ----

    [Fact]
    public async Task ListModels_returns_sorted_ids()
    {
        var (client, handler) = Create(() => Json(HttpStatusCode.OK,
            """{"object":"list","data":[{"id":"o3-mini"},{"id":"gpt-4o"},{"id":"GPT-4.1"},{"id":"dall-e-3"}]}"""));

        var models = await client.ListModelsAsync("https://api.openai.com/v1/", " sk-test ");

        Assert.Equal(["dall-e-3", "GPT-4.1", "gpt-4o", "o3-mini"], models);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://api.openai.com/v1/models", sent.Uri.ToString());
        Assert.Equal("Bearer sk-test", sent.Authorization);
    }

    [Fact]
    public async Task ListModels_maps_401()
    {
        var (client, _) = Create(() => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key"}}"""));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => client.ListModelsAsync(OpenAiBase, "bad"));

        Assert.Equal("OpenAI rejected the API key (401). Check it in Settings.", ex.Message);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task ListModels_404_points_at_the_base_url()
    {
        var (client, _) = Create(() => Json(HttpStatusCode.NotFound, """{"error":{"message":"Invalid URL (GET /models)"}}"""));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => client.ListModelsAsync("https://api.openai.com", "k"));

        Assert.Equal("OpenAI error 404: Invalid URL (GET /models). Check the base URL in Settings.", ex.Message);
    }

    [Fact]
    public async Task ListModels_with_unexpected_body_throws()
    {
        var (client, _) = Create(() => Json(HttpStatusCode.OK, "<html>login page</html>"));

        await Assert.ThrowsAsync<ChatClientException>(() => client.ListModelsAsync(OpenAiBase, "k"));
    }

    [Fact]
    public async Task ListModels_network_failure_throws_could_not_reach()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var client = new OpenAiChatClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ChatClientException>(() => client.ListModelsAsync(OpenAiBase, "k"));

        Assert.Equal("Could not reach OpenAI: Connection refused", ex.Message);
    }

    [Fact]
    public async Task ListModels_cancellation_throws_operation_cancelled()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new OpenAiChatClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListModelsAsync(OpenAiBase, "k", cts.Token));
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, string? Authorization, string Accept, string? ContentType, string? Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                body));
            return await respond(request, ct);
        }
    }

    /// <summary>A response body that hands out one small chunk per read (splitting lines and UTF-8 characters),
    /// and optionally never ends — like a server that keeps the connection open.</summary>
    private sealed class ChunkedStream(IReadOnlyList<byte[]> chunks, bool hangAtEnd) : Stream
    {
        private int _chunk;
        private int _offset;

        public static ChunkedStream Split(byte[] data, int chunkSize, bool hangAtEnd) =>
            new(data.Chunk(chunkSize).ToArray(), hangAtEnd);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_chunk >= chunks.Count)
            {
                if (hangAtEnd)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            return ReadCore(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            _chunk >= chunks.Count ? 0 : ReadCore(buffer.AsSpan(offset, count));

        private int ReadCore(Span<byte> destination)
        {
            var source = chunks[_chunk].AsSpan(_offset);
            var n = Math.Min(source.Length, destination.Length);
            source[..n].CopyTo(destination);
            _offset += n;
            if (_offset >= chunks[_chunk].Length)
            {
                _chunk++;
                _offset = 0;
            }
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
