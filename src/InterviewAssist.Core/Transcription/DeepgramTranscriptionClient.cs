using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Transcription;

/// <summary>
/// Deepgram live streaming (WebSocket) client: queues PCM16 audio, coalesces it into ~100 ms frames, keeps the
/// stream alive while idle, reconnects with backoff after unexpected disconnects and raises transcript results.
/// </summary>
public sealed class DeepgramTranscriptionClient : ITranscriptionClient
{
    private const int MaxFrameBytes = AudioFormat.BytesPerSecond / 10; // 100 ms = 3200 bytes
    private const int AudioQueueCapacity = 600;
    private const int MaxKeyTerms = 50;
    private const int ReceiveBufferBytes = 16 * 1024;
    private const int MaxMessageBytes = 1024 * 1024;

    private static readonly Uri DefaultListenUri = new("wss://api.deepgram.com/v1/listen");
    private static readonly Uri DefaultProjectsUri = new("https://api.deepgram.com/v1/projects");

    private static readonly TimeSpan[] DefaultReconnectDelays =
    [
        TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];

    private static readonly ReadOnlyMemory<byte> KeepAliveMessage = "{\"type\":\"KeepAlive\"}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> FinalizeMessage = "{\"type\":\"Finalize\"}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> CloseStreamMessage = "{\"type\":\"CloseStream\"}"u8.ToArray();

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Uri _listenUri;
    private readonly Uri _projectsUri;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _finalizeLock = new();
    private readonly List<TaskCompletionSource<bool>> _finalizeWaiters = [];

    private volatile Session? _session;
    private TranscriptionState _state = TranscriptionState.Disconnected;
    private int _streamId;

    public DeepgramTranscriptionClient()
        : this(DefaultListenUri, DefaultProjectsUri)
    {
    }

    internal DeepgramTranscriptionClient(Uri listenUri, Uri projectsUri)
    {
        ArgumentNullException.ThrowIfNull(listenUri);
        ArgumentNullException.ThrowIfNull(projectsUri);
        _listenUri = listenUri;
        _projectsUri = projectsUri;
    }

    public event EventHandler<TranscriptResult>? ResultReceived;

    public event EventHandler? UtteranceEnd;

    public event EventHandler<TranscriptionState>? StateChanged;

    public event EventHandler<string>? Error;

    public TranscriptionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Id of the current (or last) WebSocket connection: 0 before the first connect, then 1, 2, ...</summary>
    public int StreamId => Volatile.Read(ref _streamId);

    /// <summary>A KeepAlive is sent when no audio went out for this long.</summary>
    internal TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Delay before each reconnect attempt; the count is the number of attempts.</summary>
    internal IReadOnlyList<TimeSpan> ReconnectDelays { get; set; } = DefaultReconnectDelays;

    internal TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long StopAsync waits for the server to close after CloseStream.</summary>
    internal TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long StopAsync lets already-queued audio drain before sending CloseStream.</summary>
    internal TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public async Task StartAsync(TranscriptionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("Add your Deepgram API key in Settings.");
        }

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var session = new Session(options with { ApiKey = options.ApiKey.Trim() }, BuildListenUri(_listenUri, options));
            SetState(TranscriptionState.Connecting);

            ConnectResult result;
            try
            {
                result = await ConnectAsync(session, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.Dispose();
                SetState(TranscriptionState.Disconnected);
                throw;
            }

            if (result.Socket is not { } socket)
            {
                session.Dispose();
                SetState(TranscriptionState.Error);
                throw new InvalidOperationException(
                    result.AuthFailed ? result.Error : $"Could not connect to Deepgram: {result.Error}");
            }

            var streamId = Interlocked.Increment(ref _streamId);
            session.SetConnected(socket);
            _session = session;
            SetState(TranscriptionState.Connected);

            session.SendLoop = Task.Run(() => SendLoopAsync(session));
            session.ConnectionLoop = Task.Run(() => ConnectionLoopAsync(session, socket, streamId));
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void SendAudio(ReadOnlyMemory<byte> pcm16)
    {
        if (pcm16.IsEmpty)
        {
            return;
        }

        var session = _session;
        if (session is null || session.Stopping)
        {
            return;
        }

        // Copy: callers may reuse their buffer. DropOldest keeps the queue bounded while reconnecting.
        session.Audio.Writer.TryWrite(pcm16.ToArray());
    }

    public async Task<bool> FinalizeAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var session = _session;
        var socket = session?.Socket;
        if (session is null || socket is null || session.Stopping || State != TranscriptionState.Connected)
        {
            return false;
        }

        // Register before sending so a fast reply can't be missed.
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_finalizeLock)
        {
            _finalizeWaiters.Add(waiter);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            // WaitAsync bounds a stalled send without cancelling it (cancelling a WebSocket send aborts the socket).
            var sent = await SendMessageAsync(
                    session, socket, FinalizeMessage, WebSocketMessageType.Text, timeout, session.Token)
                .WaitAsync(timeout)
                .ConfigureAwait(false);
            if (!sent)
            {
                return false;
            }

            var remaining = timeout;
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }
            }

            return await waiter.Task.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            lock (_finalizeLock)
            {
                _finalizeWaiters.Remove(waiter);
            }
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public async Task<(bool Ok, string Message)> TestApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "Enter a Deepgram API key first.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _projectsUri);
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey.Trim()}");
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => (true, "Deepgram key works."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (false, "Deepgram rejected this key."),
                var code => (false, $"Deepgram returned {(int)code}."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, "Network error: the request timed out.");
        }
        catch (Exception ex)
        {
            return (false, $"Network error: {ex.Message}");
        }
    }

    internal static Uri BuildListenUri(Uri baseUri, TranscriptionOptions options)
    {
        var model = string.IsNullOrWhiteSpace(options.Model) ? "nova-3" : options.Model.Trim();
        var language = string.IsNullOrWhiteSpace(options.Language) ? "en" : options.Language.Trim();
        var endpointing = options.EndpointingMs > 0
            ? options.EndpointingMs.ToString(CultureInfo.InvariantCulture)
            : "false";

        var builder = new StringBuilder(baseUri.GetLeftPart(UriPartial.Path));
        builder.Append("?model=").Append(Uri.EscapeDataString(model))
            .Append("&language=").Append(Uri.EscapeDataString(language))
            .Append("&encoding=linear16")
            .Append("&sample_rate=").Append(AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture))
            .Append("&channels=").Append(AudioFormat.Channels.ToString(CultureInfo.InvariantCulture))
            .Append("&interim_results=true&smart_format=true&punctuate=true")
            .Append("&endpointing=").Append(Uri.EscapeDataString(endpointing))
            .Append("&utterance_end_ms=1000&vad_events=true");

        if (model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase) && options.KeyTerms is { } keyTerms)
        {
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in keyTerms)
            {
                var trimmed = term?.Trim();
                if (string.IsNullOrEmpty(trimmed) || !added.Add(trimmed))
                {
                    continue;
                }

                builder.Append("&keyterm=").Append(Uri.EscapeDataString(trimmed));
                if (added.Count == MaxKeyTerms)
                {
                    break;
                }
            }
        }

        return new Uri(builder.ToString());
    }

    private async Task StopCoreAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _session = null;
        session.Stopping = true;
        session.Audio.Writer.TryComplete();

        if (session.Socket is not null)
        {
            // Let audio that is already queued go out so the last words still get transcribed.
            await WaitQuietlyAsync(session.SendLoop, FlushTimeout).ConfigureAwait(false);
        }

        session.CancelSending();
        await WaitQuietlyAsync(session.SendLoop, Timeout.InfiniteTimeSpan).ConfigureAwait(false);

        if (session.Socket is { State: WebSocketState.Open } socket)
        {
            bool sent;
            using (var sendCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
            {
                sendCts.CancelAfter(CloseTimeout);
                try
                {
                    sent = await SendMessageAsync(
                        session, socket, CloseStreamMessage, WebSocketMessageType.Text, CloseTimeout, sendCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    sent = false;
                }
            }

            if (sent)
            {
                // The server flushes its final results and then closes; results keep being raised meanwhile.
                await WaitQuietlyAsync(session.ConnectionLoop, CloseTimeout).ConfigureAwait(false);
            }
        }

        if (session.LastSocket is { State: WebSocketState.Open or WebSocketState.CloseReceived } last)
        {
            await CloseOutputQuietlyAsync(session, last).ConfigureAwait(false);
        }

        session.Cancel();
        session.LastSocket?.Abort();
        await WaitQuietlyAsync(session.ConnectionLoop, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        session.Dispose();

        CompleteFinalizeWaiters();
        SetState(TranscriptionState.Disconnected);
    }

    /// <summary>Completes the close handshake politely; best effort, the socket is aborted afterwards anyway.</summary>
    private static async Task CloseOutputQuietlyAsync(Session session, ClientWebSocket socket)
    {
        var timeout = TimeSpan.FromMilliseconds(500);
        if (!await session.SendLock.WaitAsync(timeout).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            using var closeCts = new CancellationTokenSource(timeout);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    /// <summary>Runs the receive loop for each connection and reconnects after unexpected disconnects.</summary>
    private async Task ConnectionLoopAsync(Session session, ClientWebSocket socket, int streamId)
    {
        try
        {
            while (true)
            {
                var reason = await ReceiveLoopAsync(session, socket, streamId).ConfigureAwait(false);

                session.SetDisconnected(socket);
                CompleteFinalizeWaiters();
                if (session.Stopping || session.Token.IsCancellationRequested)
                {
                    return;
                }

                socket.Abort();
                socket.Dispose();
                SetState(TranscriptionState.Reconnecting);

                var next = await ReconnectAsync(session, reason).ConfigureAwait(false);
                if (next is null)
                {
                    return;
                }

                socket = next;
                streamId = Interlocked.Increment(ref _streamId);
                session.SetConnected(socket);
                if (session.Stopping)
                {
                    // StopAsync takes over this socket (it closes LastSocket).
                    continue;
                }

                SetState(TranscriptionState.Connected);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deepgram connection loop failed: {ex}");
        }
    }

    private async Task<ClientWebSocket?> ReconnectAsync(Session session, string reason)
    {
        var lastError = reason;
        foreach (var delay in ReconnectDelays)
        {
            try
            {
                await Task.Delay(delay, session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (session.Stopping)
            {
                return null;
            }

            ConnectResult result;
            try
            {
                result = await ConnectAsync(session, session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (result.Socket is { } socket)
            {
                return socket;
            }

            if (result.AuthFailed)
            {
                GiveUp(session, result.Error);
                return null;
            }

            lastError = result.Error;
        }

        GiveUp(session, $"Lost connection to Deepgram: {lastError}");
        return null;
    }

    private void GiveUp(Session session, string message)
    {
        if (session.Stopping)
        {
            return;
        }

        session.Audio.Writer.TryComplete();
        session.CancelSending();
        SetState(TranscriptionState.Error);
        RaiseError(message);
    }

    /// <summary>Opens one WebSocket. Throws only <see cref="OperationCanceledException"/> (when <paramref name="ct"/> fires).</summary>
    private async Task<ConnectResult> ConnectAsync(Session session, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", $"Token {session.Options.ApiKey}");
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(session.Uri, timeoutCts.Token).ConfigureAwait(false);
            socket.HttpResponseHeaders = null; // only needed for handshake failures
            return new ConnectResult(socket, "", AuthFailed: false);
        }
        catch (Exception ex)
        {
            var status = (int)socket.HttpStatusCode;
            var serverError = GetServerError(socket);
            socket.Dispose();

            ct.ThrowIfCancellationRequested();

            if (status is 401 or 403)
            {
                return new ConnectResult(null, $"Deepgram rejected the API key ({status}). Check it in Settings.", AuthFailed: true);
            }

            var detail = ex switch
            {
                OperationCanceledException or WebSocketException { InnerException: OperationCanceledException }
                    => "the connection timed out.",
                _ when serverError is not null => status > 0 ? $"{serverError} ({status})" : serverError,
                WebSocketException { InnerException: { } inner } => inner.Message,
                _ => ex.Message,
            };
            return new ConnectResult(null, detail, AuthFailed: false);
        }
    }

    /// <summary>Deepgram explains handshake rejections (bad model, language, ...) in a dg-error response header.</summary>
    private static string? GetServerError(ClientWebSocket socket)
    {
        if (socket.HttpResponseHeaders is not { } headers)
        {
            return null;
        }

        foreach (var (name, values) in headers)
        {
            if (!string.Equals(name, "dg-error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>Receives until the socket closes or fails; returns a short reason.</summary>
    private async Task<string> ReceiveLoopAsync(Session session, ClientWebSocket socket, int streamId)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        var message = new ArrayBufferWriter<byte>(ReceiveBufferBytes);
        var oversized = false;
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), session.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return DescribeClose(socket);
                }

                if (!oversized && message.WrittenCount + received.Count <= MaxMessageBytes)
                {
                    message.Write(buffer.AsSpan(0, received.Count));
                }
                else
                {
                    oversized = true;
                }

                if (!received.EndOfMessage)
                {
                    continue;
                }

                if (received.MessageType == WebSocketMessageType.Text && !oversized)
                {
                    HandleMessage(Encoding.UTF8.GetString(message.WrittenSpan), streamId);
                }

                message.ResetWrittenCount();
                oversized = false;
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception ex)
        {
            return ex is WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string DescribeClose(ClientWebSocket socket)
    {
        var description = socket.CloseStatusDescription;
        var status = socket.CloseStatus is { } code ? ((int)code).ToString(CultureInfo.InvariantCulture) : "no status";
        return string.IsNullOrWhiteSpace(description)
            ? $"the server closed the connection ({status})."
            : $"the server closed the connection ({status}: {description.Trim()}).";
    }

    private void HandleMessage(string json, int streamId)
    {
        var message = DeepgramMessageParser.Parse(json, streamId);
        switch (message.Kind)
        {
            case DeepgramMessageKind.Results:
                if (message.Result is { } result && !string.IsNullOrWhiteSpace(result.Text))
                {
                    Raise(ResultReceived, result);
                }

                // Completed after the result was raised, so FinalizeAsync callers see it already applied.
                if (message.FromFinalize)
                {
                    CompleteFirstFinalizeWaiter();
                }

                break;

            case DeepgramMessageKind.UtteranceEnd:
                Raise(UtteranceEnd);
                break;

            case DeepgramMessageKind.Error:
                RaiseError($"Deepgram error: {message.ErrorMessage}");
                break;
        }
    }

    /// <summary>Sends audio frames and KeepAlives on the current connection; waits (keeping audio queued) while reconnecting.</summary>
    private async Task SendLoopAsync(Session session)
    {
        var reader = session.Audio.Reader;
        var token = session.SendToken;
        var buffer = new byte[MaxFrameBytes];
        ReadOnlyMemory<byte> frame = default;
        ReadOnlyMemory<byte> carry = default;
        ClientWebSocket? previousSocket = null;
        var lastSent = Stopwatch.GetTimestamp();

        try
        {
            while (true)
            {
                var socket = await session.WaitConnectedAsync(token).ConfigureAwait(false);
                if (!ReferenceEquals(socket, previousSocket))
                {
                    previousSocket = socket;
                    lastSent = Stopwatch.GetTimestamp();
                }

                if (frame.IsEmpty)
                {
                    if (carry.IsEmpty && !reader.TryPeek(out _))
                    {
                        var idle = Stopwatch.GetElapsedTime(lastSent);
                        if (idle >= KeepAliveInterval)
                        {
                            await SendMessageAsync(
                                session, socket, KeepAliveMessage, WebSocketMessageType.Text,
                                Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                            lastSent = Stopwatch.GetTimestamp();
                            continue;
                        }

                        if (await WaitForAudioAsync(reader, KeepAliveInterval - idle, token).ConfigureAwait(false) == false)
                        {
                            return; // stopped and fully drained
                        }

                        continue;
                    }

                    frame = BuildFrame(reader, buffer, ref carry);
                    if (frame.IsEmpty)
                    {
                        continue; // never send an empty binary message: Deepgram treats it as end of stream
                    }
                }

                var sent = await SendMessageAsync(
                    session, socket, frame, WebSocketMessageType.Binary, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                if (sent)
                {
                    frame = default;
                    lastSent = Stopwatch.GetTimestamp();
                }

                // Otherwise the frame is kept and sent on the next connection.
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deepgram send loop failed: {ex}");
        }
    }

    /// <summary>Coalesces queued chunks into one frame of up to <see cref="MaxFrameBytes"/> (a bigger single chunk is sent as is).</summary>
    private static ReadOnlyMemory<byte> BuildFrame(
        ChannelReader<ReadOnlyMemory<byte>> reader, byte[] buffer, ref ReadOnlyMemory<byte> carry)
    {
        var length = 0;
        while (true)
        {
            ReadOnlyMemory<byte> chunk;
            if (!carry.IsEmpty)
            {
                chunk = carry;
                carry = default;
            }
            else if (!reader.TryRead(out chunk))
            {
                break;
            }

            if (length == 0 && chunk.Length >= buffer.Length)
            {
                return chunk;
            }

            if (length + chunk.Length > buffer.Length)
            {
                carry = chunk;
                break;
            }

            chunk.Span.CopyTo(buffer.AsSpan(length));
            length += chunk.Length;
            if (length == buffer.Length)
            {
                break;
            }
        }

        return buffer.AsMemory(0, length);
    }

    /// <summary>Returns true when audio is available, false when the queue is completed and empty, null on timeout.</summary>
    private static async Task<bool?> WaitForAudioAsync(
        ChannelReader<ReadOnlyMemory<byte>> reader, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends one message with the session's send lock held (only one SendAsync may be in flight). Returns false
    /// when the lock wasn't acquired in time or the connection failed (it is then aborted so the loop reconnects).
    /// </summary>
    private static async Task<bool> SendMessageAsync(
        Session session,
        ClientWebSocket socket,
        ReadOnlyMemory<byte> data,
        WebSocketMessageType type,
        TimeSpan lockTimeout,
        CancellationToken token)
    {
        if (!await session.SendLock.WaitAsync(lockTimeout, token).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await socket.SendAsync(data, type, endOfMessage: true, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (session.SetDisconnected(socket))
            {
                socket.Abort();
            }

            return false;
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    private static async Task WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timeouts and loop failures are expected while shutting down.
        }
    }

    private void CompleteFirstFinalizeWaiter()
    {
        TaskCompletionSource<bool>? waiter = null;
        lock (_finalizeLock)
        {
            if (_finalizeWaiters.Count > 0)
            {
                waiter = _finalizeWaiters[0];
                _finalizeWaiters.RemoveAt(0);
            }
        }

        waiter?.TrySetResult(true);
    }

    private void CompleteFinalizeWaiters()
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_finalizeLock)
        {
            waiters = _finalizeWaiters.ToArray();
            _finalizeWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(false);
        }
    }

    private void SetState(TranscriptionState state)
    {
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        Raise(StateChanged, state);
    }

    private void RaiseError(string message) => Raise(Error, message);

    // A throwing subscriber must not take down the receive loop.
    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deepgram client event handler failed: {ex}");
        }
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deepgram client event handler failed: {ex}");
        }
    }

    private readonly record struct ConnectResult(ClientWebSocket? Socket, string Error, bool AuthFailed);

    /// <summary>One StartAsync..StopAsync lifetime: the audio queue, loops and the current socket.</summary>
    private sealed class Session : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationTokenSource _sendCts;
        private ClientWebSocket? _socket;
        private ClientWebSocket? _lastSocket;
        private TaskCompletionSource<ClientWebSocket> _connected = NewConnectedSignal();
        private volatile bool _stopping;

        public Session(TranscriptionOptions options, Uri uri)
        {
            Options = options;
            Uri = uri;
            Token = _cts.Token;
            _sendCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            SendToken = _sendCts.Token;
        }

        public TranscriptionOptions Options { get; }

        public Uri Uri { get; }

        public Channel<ReadOnlyMemory<byte>> Audio { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(AudioQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        /// <summary>Cancels everything (receive, reconnect, sends).</summary>
        public CancellationToken Token { get; }

        /// <summary>Cancels the send loop only.</summary>
        public CancellationToken SendToken { get; }

        public Task SendLoop { get; set; } = Task.CompletedTask;

        public Task ConnectionLoop { get; set; } = Task.CompletedTask;

        public bool Stopping
        {
            get => _stopping;
            set => _stopping = value;
        }

        /// <summary>The connected socket, or null while (re)connecting or after the connection was lost.</summary>
        public ClientWebSocket? Socket
        {
            get
            {
                lock (_gate)
                {
                    return _socket;
                }
            }
        }

        /// <summary>The most recently connected socket (owned by the session until disposed).</summary>
        public ClientWebSocket? LastSocket
        {
            get
            {
                lock (_gate)
                {
                    return _lastSocket;
                }
            }
        }

        public void SetConnected(ClientWebSocket socket)
        {
            lock (_gate)
            {
                _socket = socket;
                _lastSocket = socket;
                if (!_connected.TrySetResult(socket))
                {
                    _connected = NewConnectedSignal();
                    _connected.TrySetResult(socket);
                }
            }
        }

        /// <summary>Marks <paramref name="socket"/> as gone. Returns false if it wasn't the current socket.</summary>
        public bool SetDisconnected(ClientWebSocket socket)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_socket, socket))
                {
                    return false;
                }

                _socket = null;
                _connected = NewConnectedSignal();
                return true;
            }
        }

        public Task<ClientWebSocket> WaitConnectedAsync(CancellationToken ct)
        {
            Task<ClientWebSocket> task;
            lock (_gate)
            {
                task = _connected.Task;
            }

            return task.WaitAsync(ct);
        }

        public void CancelSending() => TryCancel(_sendCts);

        public void Cancel() => TryCancel(_cts);

        public void Dispose()
        {
            TryCancel(_cts);
            ClientWebSocket? socket;
            lock (_gate)
            {
                socket = _lastSocket;
                _socket = null;
            }

            socket?.Abort();
            socket?.Dispose();
            _sendCts.Dispose();
            _cts.Dispose();
        }

        private static void TryCancel(CancellationTokenSource cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static TaskCompletionSource<ClientWebSocket> NewConnectedSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
