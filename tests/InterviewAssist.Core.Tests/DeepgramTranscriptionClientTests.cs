using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using InterviewAssist.Core.Models;
using InterviewAssist.Core.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Core.Tests;

public class DeepgramTranscriptionClientTests
{
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(50);

    private static TranscriptionOptions Options(IReadOnlyList<string>? keyTerms = null, string model = "nova-3") =>
        new("test-key", model, "en-US", 300, keyTerms ?? []);

    private static DeepgramTranscriptionClient CreateClient(FakeDeepgramServer server) =>
        new(server.ListenUri, server.ProjectsUri)
        {
            // Tests that exercise KeepAlive lower this; elsewhere it would only add noise to the message log.
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            ReconnectDelays = [FastRetry, FastRetry, FastRetry, FastRetry, FastRetry],
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

    private static string ResultsJson(string transcript, bool isFinal, bool fromFinalize = false, double start = 0, double duration = 1) =>
        $$$"""
        {"type":"Results","channel_index":[0,1],"start":{{{start.ToString(CultureInfo.InvariantCulture)}}},"duration":{{{duration.ToString(CultureInfo.InvariantCulture)}}},"is_final":{{{Bool(isFinal)}}},"speech_final":{{{Bool(isFinal)}}},"from_finalize":{{{Bool(fromFinalize)}}},"channel":{"alternatives":[{"transcript":"{{{transcript}}}","confidence":0.97}]}}
        """;

    private static string Bool(bool value) => value ? "true" : "false";

    [Fact]
    public async Task StartAsync_ForwardsAuthHeaderAndQueryParameters()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);

        await client.StartAsync(Options(keyTerms: ["Kubernetes", "  ", "C# / .NET", "kubernetes"]));
        var connection = await server.WaitForConnectionAsync(1);

        Assert.Equal("Token test-key", connection.Authorization);
        Assert.Equal(new[] { "nova-3" }, connection.Query["model"]);
        Assert.Equal(new[] { "en-US" }, connection.Query["language"]);
        Assert.Equal(new[] { "linear16" }, connection.Query["encoding"]);
        Assert.Equal(new[] { "16000" }, connection.Query["sample_rate"]);
        Assert.Equal(new[] { "1" }, connection.Query["channels"]);
        Assert.Equal(new[] { "true" }, connection.Query["interim_results"]);
        Assert.Equal(new[] { "300" }, connection.Query["endpointing"]);
        Assert.Equal(new[] { "1000" }, connection.Query["utterance_end_ms"]);
        Assert.Equal(new[] { "Kubernetes", "C# / .NET" }, connection.Query["keyterm"]);
        Assert.Equal(TranscriptionState.Connected, client.State);
        Assert.Equal(1, client.StreamId);
    }

    [Fact]
    public void BuildListenUri_EscapesValues_AndOnlyAddsKeyTermsForNova3()
    {
        var baseUri = new Uri("wss://api.deepgram.com/v1/listen");
        var terms = Enumerable.Range(0, 60).Select(i => $"term {i}").Append("A&B=C").ToArray();

        var nova3 = DeepgramTranscriptionClient.BuildListenUri(baseUri, new("k", "nova-3-general", "en", 250, terms)).AbsoluteUri;
        var nova2 = DeepgramTranscriptionClient.BuildListenUri(baseUri, new("k", "nova-2", "multi", 250, terms)).AbsoluteUri;

        Assert.StartsWith("wss://api.deepgram.com/v1/listen?model=nova-3-general&language=en&encoding=linear16&sample_rate=16000&channels=1", nova3);
        Assert.Contains("&interim_results=true&smart_format=true&punctuate=true&endpointing=250&utterance_end_ms=1000&vad_events=true", nova3);
        Assert.Contains("&keyterm=term%200", nova3);
        Assert.Equal(50, CountOccurrences(nova3, "&keyterm="));
        Assert.DoesNotContain("A%26B", nova3); // beyond the 50-term limit
        Assert.DoesNotContain("keyterm", nova2);
        Assert.Contains("language=multi", nova2);

        var escaped = DeepgramTranscriptionClient.BuildListenUri(baseUri, new("k", "nova-3", "en", 300, ["A&B=C"])).AbsoluteUri;
        Assert.EndsWith("&keyterm=A%26B%3DC", escaped);
    }

    [Fact]
    public async Task SendAudio_CopiesChunks_AndFramesArriveInOrder()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        // One reused buffer: the client must copy each chunk before returning.
        var buffer = new byte[640];
        var expected = new List<byte>();
        for (var i = 0; i < 12; i++)
        {
            Array.Fill(buffer, (byte)(i + 1));
            client.SendAudio(buffer);
            expected.AddRange(buffer);
        }

        await WaitUntilAsync(() => connection.BinaryByteCount >= expected.Count, "audio at the server");

        Assert.Equal(expected.ToArray(), connection.GetBinaryData());
        Assert.All(connection.GetBinaryFrameSizes(), size => Assert.InRange(size, 1, 3200));
    }

    [Fact]
    public async Task FinalizeAsync_RoundTrip_ReturnsTrueAfterResultWasRaised()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        server.OnText = async (connection, json) =>
        {
            if (FakeDeepgramServer.TypeOf(json) == "Finalize")
            {
                await connection.SendTextAsync(ResultsJson("And why this company?", isFinal: true, fromFinalize: true, start: 2, duration: 1.5));
            }
        };
        await using var client = CreateClient(server);
        var events = new ConcurrentQueue<string>();
        var results = new ConcurrentQueue<TranscriptResult>();
        client.ResultReceived += (_, result) =>
        {
            results.Enqueue(result);
            events.Enqueue("result");
        };
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        var finalized = await client.FinalizeAsync(TimeSpan.FromSeconds(5));
        events.Enqueue("finalized");

        Assert.True(finalized);
        Assert.Equal(new[] { "result", "finalized" }, events.ToArray());
        var result = Assert.Single(results);
        Assert.Equal("And why this company?", result.Text);
        Assert.True(result.IsFinal);
        Assert.True(result.FromFinalize);
        Assert.Equal(1, result.StreamId);
        Assert.Equal(3.5, result.End, 6);
        Assert.Contains(connection.GetTextMessages(), m => FakeDeepgramServer.TypeOf(m) == "Finalize");
    }

    [Fact]
    public async Task FinalizeAsync_EmptyFromFinalizeResult_CompletesWithoutRaisingResult()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        server.OnText = async (connection, json) =>
        {
            if (FakeDeepgramServer.TypeOf(json) == "Finalize")
            {
                await connection.SendTextAsync(ResultsJson("", isFinal: true, fromFinalize: true));
            }
        };
        await using var client = CreateClient(server);
        var results = 0;
        client.ResultReceived += (_, _) => Interlocked.Increment(ref results);
        await client.StartAsync(Options());

        Assert.True(await client.FinalizeAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, results);
    }

    [Fact]
    public async Task FinalizeAsync_ReturnsFalse_OnTimeoutOrWhenNotConnected()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);

        Assert.False(await client.FinalizeAsync(TimeSpan.FromSeconds(1)));

        await client.StartAsync(Options());
        var stopwatch = Stopwatch.StartNew();
        Assert.False(await client.FinalizeAsync(TimeSpan.FromMilliseconds(150)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task KeepAlive_IsSentWhileIdle()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        client.KeepAliveInterval = TimeSpan.FromMilliseconds(100);
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        await WaitUntilAsync(
            () => connection.GetTextMessages().Count(m => FakeDeepgramServer.TypeOf(m) == "KeepAlive") >= 2,
            "two KeepAlive messages");

        Assert.Equal(0, connection.BinaryByteCount);
    }

    [Fact]
    public async Task ServerDrop_ClientReconnects_WithNewStreamId_AndKeepsQueuedAudio()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        client.ReconnectDelays = [FastRetry, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)];
        var states = new ConcurrentQueue<TranscriptionState>();
        var results = new ConcurrentQueue<TranscriptResult>();
        client.StateChanged += (_, state) => states.Enqueue(state);
        client.ResultReceived += (_, result) => results.Enqueue(result);
        await client.StartAsync(Options());
        var first = await server.WaitForConnectionAsync(1);

        server.RejectStatus = 503;
        first.Abort();
        await WaitUntilAsync(() => server.RejectedHandshakes >= 1, "the first reconnect attempt");
        Assert.Equal(TranscriptionState.Reconnecting, client.State);

        // Audio sent while reconnecting stays queued and goes out on the new connection.
        var audio = Enumerable.Range(0, 960).Select(i => (byte)i).ToArray();
        client.SendAudio(audio);
        server.RejectStatus = 0;

        var second = await server.WaitForConnectionAsync(2);
        await WaitUntilAsync(() => client.State == TranscriptionState.Connected, "Connected again");
        Assert.Equal(2, client.StreamId);
        Assert.Equal(
            new[] { TranscriptionState.Connecting, TranscriptionState.Connected, TranscriptionState.Reconnecting, TranscriptionState.Connected },
            states.ToArray());

        await WaitUntilAsync(() => second.BinaryByteCount >= audio.Length, "queued audio on the new connection");
        Assert.Equal(audio, second.GetBinaryData());

        await second.SendTextAsync(ResultsJson("after the reconnect", isFinal: true));
        await WaitUntilAsync(() => !results.IsEmpty, "a result from the new stream");
        Assert.Equal(2, Assert.Single(results).StreamId);
    }

    [Fact]
    public async Task Handshake401_StartThrows_AndDoesNotRetry()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        server.RejectStatus = 401;
        await using var client = CreateClient(server);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(Options()));

        Assert.Equal("Deepgram rejected the API key (401). Check it in Settings.", error.Message);
        Assert.Equal(TranscriptionState.Error, client.State);
        Assert.Equal(0, client.StreamId);
        await Task.Delay(FastRetry * 4); // a retry, if any, would have happened by now
        Assert.Equal(1, server.HandshakeAttempts);
    }

    [Fact]
    public async Task HandshakeRejected_WithDeepgramErrorHeader_ReportsIt()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        server.RejectStatus = 400;
        server.RejectErrorHeader = "Bad Request: unknown model";
        await using var client = CreateClient(server);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(Options()));

        Assert.Equal("Could not connect to Deepgram: Bad Request: unknown model (400)", error.Message);
    }

    [Fact]
    public async Task AuthFailureWhileReconnecting_StopsRetrying()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        var errors = new ConcurrentQueue<string>();
        client.Error += (_, message) => errors.Enqueue(message);
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        server.RejectStatus = 403;
        connection.Abort();
        await WaitUntilAsync(() => client.State == TranscriptionState.Error, "the Error state");

        Assert.Equal("Deepgram rejected the API key (403). Check it in Settings.", Assert.Single(errors));
        await Task.Delay(FastRetry * 4);
        Assert.Equal(2, server.HandshakeAttempts);
    }

    [Fact]
    public async Task Reconnect_GivesUpAfterFiveAttempts()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        client.ReconnectDelays = Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 5).ToArray();
        var errors = new ConcurrentQueue<string>();
        client.Error += (_, message) => errors.Enqueue(message);
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        server.RejectStatus = 500;
        connection.Abort();
        await WaitUntilAsync(() => client.State == TranscriptionState.Error, "the Error state");

        Assert.Equal(6, server.HandshakeAttempts);
        Assert.StartsWith("Lost connection to Deepgram: ", Assert.Single(errors));
        Assert.False(await client.FinalizeAsync(TimeSpan.FromMilliseconds(100)));

        await client.StopAsync();
        Assert.Equal(TranscriptionState.Disconnected, client.State);
    }

    [Fact]
    public async Task StopAsync_FlushesAudio_SendsCloseStream_RaisesLateResults_AndIsIdempotent()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        server.OnText = async (connection, json) =>
        {
            if (FakeDeepgramServer.TypeOf(json) == "CloseStream")
            {
                await connection.SendTextAsync(ResultsJson("last words", isFinal: true));
            }
        };
        await using var client = CreateClient(server);
        var results = new ConcurrentQueue<TranscriptResult>();
        var states = new ConcurrentQueue<TranscriptionState>();
        client.ResultReceived += (_, result) => results.Enqueue(result);
        await client.StartAsync(Options());
        client.StateChanged += (_, state) => states.Enqueue(state);
        var connection = await server.WaitForConnectionAsync(1);

        for (var i = 0; i < 3; i++)
        {
            client.SendAudio(new byte[640]);
        }

        await client.StopAsync();

        Assert.Equal(TranscriptionState.Disconnected, client.State);
        Assert.Equal("last words", Assert.Single(results).Text);
        Assert.Equal(1920, connection.BinaryByteCount);
        var log = connection.GetMessageLog();
        var closeIndex = log.FindIndex(m => m.Type == WebSocketMessageType.Text && FakeDeepgramServer.TypeOf(m.Text) == "CloseStream");
        Assert.True(closeIndex >= 0, "CloseStream was not sent");
        Assert.True(log.FindLastIndex(m => m.Type == WebSocketMessageType.Binary) < closeIndex, "audio must be flushed before CloseStream");

        await client.StopAsync();
        client.SendAudio(new byte[640]);

        Assert.Equal(new[] { TranscriptionState.Disconnected }, states.ToArray());
        Assert.Single(connection.GetTextMessages(), m => FakeDeepgramServer.TypeOf(m) == "CloseStream");
        Assert.Equal(1, server.HandshakeAttempts);
        await WaitUntilAsync(() => connection.IsCompleted, "the server side of the socket to close");
    }

    [Fact]
    public async Task StopAsync_WhenNeverStarted_IsANoOp()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        var stateChanges = 0;
        client.StateChanged += (_, _) => stateChanges++;

        await client.StopAsync();
        await client.StopAsync();

        Assert.Equal(TranscriptionState.Disconnected, client.State);
        Assert.Equal(0, stateChanges);
        Assert.Equal(0, server.HandshakeAttempts);
    }

    [Fact]
    public async Task ServerMessages_RaiseResultsUtteranceEndAndErrors()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        var results = new ConcurrentQueue<TranscriptResult>();
        var errors = new ConcurrentQueue<string>();
        var utteranceEnds = 0;
        client.ResultReceived += (_, result) => results.Enqueue(result);
        client.Error += (_, message) => errors.Enqueue(message);
        client.UtteranceEnd += (_, _) => Interlocked.Increment(ref utteranceEnds);
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        await connection.SendTextAsync("""{"type":"Metadata","request_id":"abc"}""");
        await connection.SendTextAsync("""{"type":"SpeechStarted","channel":[0,1],"timestamp":0.2}""");
        await connection.SendTextAsync(ResultsJson("tell me", isFinal: false, start: 0.2, duration: 0.6));
        await connection.SendTextAsync(ResultsJson("  ", isFinal: true));
        await connection.SendTextAsync("""{"type":"UtteranceEnd","channel":[0,1],"last_word_end":0.8}""");
        await connection.SendTextAsync("this is not json");
        await connection.SendTextAsync("""{"type":"Error","description":"Failed to decode audio."}""");
        await WaitUntilAsync(() => !errors.IsEmpty, "the error message");

        var result = Assert.Single(results);
        Assert.Equal("tell me", result.Text);
        Assert.False(result.IsFinal);
        Assert.Equal(1, result.StreamId);
        Assert.Equal(1, Volatile.Read(ref utteranceEnds));
        Assert.Equal("Deepgram error: Failed to decode audio.", Assert.Single(errors));
        Assert.Equal(TranscriptionState.Connected, client.State);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotBreakTheConnection()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        var received = 0;
        client.ResultReceived += (_, _) =>
        {
            Interlocked.Increment(ref received);
            throw new InvalidOperationException("subscriber bug");
        };
        await client.StartAsync(Options());
        var connection = await server.WaitForConnectionAsync(1);

        await connection.SendTextAsync(ResultsJson("one", isFinal: true));
        await connection.SendTextAsync(ResultsJson("two", isFinal: true));
        await WaitUntilAsync(() => Volatile.Read(ref received) == 2, "both results");

        Assert.Equal(TranscriptionState.Connected, client.State);
        Assert.Equal(1, server.HandshakeAttempts);
    }

    [Fact]
    public async Task StartAsync_WithBlankKey_Throws()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(new TranscriptionOptions("  ", "nova-3", "en", 300, [])));

        Assert.Equal(0, server.HandshakeAttempts);
        Assert.Equal(TranscriptionState.Disconnected, client.State);
    }

    [Fact]
    public async Task StartAsync_WhileRunning_RestartsOnANewStream()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);
        await client.StartAsync(Options());
        var first = await server.WaitForConnectionAsync(1);

        await client.StartAsync(Options());
        var second = await server.WaitForConnectionAsync(2);

        Assert.Equal(2, client.StreamId);
        Assert.Equal(TranscriptionState.Connected, client.State);
        Assert.Contains(first.GetTextMessages(), m => FakeDeepgramServer.TypeOf(m) == "CloseStream");
        client.SendAudio(new byte[320]);
        await WaitUntilAsync(() => second.BinaryByteCount == 320, "audio on the new stream");
        Assert.Equal(0, first.BinaryByteCount);
    }

    [Fact]
    public async Task TestApiKeyAsync_MapsResponses()
    {
        await using var server = await FakeDeepgramServer.StartAsync();
        await using var client = CreateClient(server);

        Assert.Equal((true, "Deepgram key works."), await client.TestApiKeyAsync(" good "));
        Assert.Equal("Token good", server.LastProjectsAuthorization);
        Assert.Equal((false, "Deepgram rejected this key."), await client.TestApiKeyAsync("bad"));
        Assert.Equal((false, "Deepgram rejected this key."), await client.TestApiKeyAsync("forbidden"));
        Assert.Equal((false, "Deepgram returned 500."), await client.TestApiKeyAsync("boom"));

        var (ok, message) = await client.TestApiKeyAsync("drop");
        Assert.False(ok);
        Assert.StartsWith("Network error: ", message);

        Assert.False((await client.TestApiKeyAsync("   ")).Ok);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>A minimal stand-in for Deepgram's /v1/listen WebSocket and /v1/projects endpoints, hosted on Kestrel.</summary>
internal sealed class FakeDeepgramServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<FakeDeepgramConnection> _connections = [];
    private int _handshakeAttempts;
    private int _rejectedHandshakes;
    private volatile int _rejectStatus;
    private volatile string? _rejectErrorHeader;
    private volatile string? _lastProjectsAuthorization;

    private FakeDeepgramServer(WebApplication app)
    {
        _app = app;
    }

    public Uri ListenUri { get; private set; } = null!;

    public Uri ProjectsUri { get; private set; } = null!;

    /// <summary>Non-zero: WebSocket handshakes are answered with this HTTP status instead of being accepted.</summary>
    public int RejectStatus
    {
        get => _rejectStatus;
        set => _rejectStatus = value;
    }

    public string? RejectErrorHeader
    {
        get => _rejectErrorHeader;
        set => _rejectErrorHeader = value;
    }

    /// <summary>Called for every text message a connection receives (before the automatic CloseStream handling).</summary>
    public Func<FakeDeepgramConnection, string, Task>? OnText { get; set; }

    public int HandshakeAttempts => Volatile.Read(ref _handshakeAttempts);

    public int RejectedHandshakes => Volatile.Read(ref _rejectedHandshakes);

    public string? LastProjectsAuthorization => _lastProjectsAuthorization;

    public static async Task<FakeDeepgramServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeDeepgramServer(app);
        app.UseWebSockets();
        app.Run(server.HandleAsync);
        await app.StartAsync();

        var bound = new Uri(app.Urls.First());
        server.ListenUri = new Uri($"ws://127.0.0.1:{bound.Port}/v1/listen");
        server.ProjectsUri = new Uri($"http://127.0.0.1:{bound.Port}/v1/projects");
        return server;
    }

    public static string? TypeOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<FakeDeepgramConnection> WaitForConnectionAsync(int number)
    {
        FakeDeepgramConnection? connection = null;
        await DeepgramTranscriptionClientTests.WaitUntilAsync(
            () =>
            {
                lock (_gate)
                {
                    connection = _connections.Count >= number ? _connections[number - 1] : null;
                }

                return connection is not null;
            },
            $"WebSocket connection #{number}");
        return connection!;
    }

    public async ValueTask DisposeAsync()
    {
        FakeDeepgramConnection[] connections;
        lock (_gate)
        {
            connections = _connections.ToArray();
        }

        foreach (var connection in connections)
        {
            connection.Abort();
        }

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _app.StopAsync(stopCts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        switch (context.Request.Path.Value)
        {
            case "/v1/projects":
                HandleProjects(context);
                return;

            case "/v1/listen":
                await HandleListenAsync(context);
                return;

            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private void HandleProjects(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        _lastProjectsAuthorization = authorization;
        switch (authorization)
        {
            case "Token good":
                context.Response.StatusCode = StatusCodes.Status200OK;
                break;
            case "Token bad":
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                break;
            case "Token forbidden":
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                break;
            case "Token drop":
                context.Abort();
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                break;
        }
    }

    private async Task HandleListenAsync(HttpContext context)
    {
        Interlocked.Increment(ref _handshakeAttempts);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var reject = RejectStatus;
        if (reject != 0)
        {
            Interlocked.Increment(ref _rejectedHandshakes);
            if (RejectErrorHeader is { } errorHeader)
            {
                context.Response.Headers["dg-error"] = errorHeader;
            }

            context.Response.StatusCode = reject;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(v => v ?? "").ToArray(),
            StringComparer.Ordinal);
        var connection = new FakeDeepgramConnection(context, socket, context.Request.Headers.Authorization.ToString(), query);
        lock (_gate)
        {
            _connections.Add(connection);
        }

        await connection.RunAsync(OnText);
    }
}

internal sealed record FakeMessage(WebSocketMessageType Type, byte[] Data)
{
    public string Text => Encoding.UTF8.GetString(Data);
}

internal sealed class FakeDeepgramConnection
{
    private readonly HttpContext _context;
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private readonly List<FakeMessage> _messages = [];
    private volatile bool _completed;

    public FakeDeepgramConnection(HttpContext context, WebSocket socket, string authorization, Dictionary<string, string[]> query)
    {
        _context = context;
        _socket = socket;
        Authorization = authorization;
        Query = query;
    }

    public string Authorization { get; }

    public IReadOnlyDictionary<string, string[]> Query { get; }

    public bool IsCompleted => _completed;

    public int BinaryByteCount
    {
        get
        {
            lock (_gate)
            {
                return _messages.Where(m => m.Type == WebSocketMessageType.Binary).Sum(m => m.Data.Length);
            }
        }
    }

    public List<FakeMessage> GetMessageLog()
    {
        lock (_gate)
        {
            return [.. _messages];
        }
    }

    public IReadOnlyList<string> GetTextMessages() =>
        GetMessageLog().Where(m => m.Type == WebSocketMessageType.Text).Select(m => m.Text).ToArray();

    public byte[] GetBinaryData() =>
        GetMessageLog().Where(m => m.Type == WebSocketMessageType.Binary).SelectMany(m => m.Data).ToArray();

    public IReadOnlyList<int> GetBinaryFrameSizes() =>
        GetMessageLog().Where(m => m.Type == WebSocketMessageType.Binary).Select(m => m.Data.Length).ToArray();

    public async Task SendTextAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Drops the TCP connection without a close handshake.</summary>
    public void Abort()
    {
        if (_completed)
        {
            return;
        }

        try
        {
            _context.Abort();
        }
        catch (ObjectDisposedException)
        {
            // The request already finished.
        }
    }

    internal async Task RunAsync(Func<FakeDeepgramConnection, string, Task>? onText)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await CloseOutputAsync();
                    return;
                }

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                {
                    continue;
                }

                var data = message.ToArray();
                message.SetLength(0);
                lock (_gate)
                {
                    _messages.Add(new FakeMessage(received.MessageType, data));
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(data);
                if (onText is not null)
                {
                    await onText(this, text);
                }

                // Like Deepgram: flush, then close the stream after CloseStream.
                if (FakeDeepgramServer.TypeOf(text) == "CloseStream")
                {
                    await CloseOutputAsync();
                }
            }
        }
        catch (Exception)
        {
            // The client went away or the connection was aborted by the test.
        }
        finally
        {
            _completed = true;
        }
    }

    private async Task CloseOutputAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
