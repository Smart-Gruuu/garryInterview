using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Chat;

/// <summary>
/// OpenAI-compatible Chat Completions client that streams answers over Server-Sent Events.
/// Stateless apart from the shared <see cref="HttpClient"/>, so one instance can serve concurrent calls.
/// </summary>
public sealed class OpenAiChatClient : IChatCompletionClient
{
    private const string OpenAiHost = "api.openai.com";

    // Upper bound for buffering a non-SSE body (a server that ignored stream:true, or a JSON error with a 2xx status).
    private const int MaxNonStreamBodyChars = 1_000_000;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // The body goes to an HTTP API, not into HTML, so relaxed escaping is safe and keeps non-Latin text compact.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;

    public OpenAiChatClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>How long to wait for the response headers. The streamed body itself has no time limit.</summary>
    internal TimeSpan HeadersTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        using var httpRequest = CreateRequest(HttpMethod.Post, request.BaseUrl, "chat/completions", request.ApiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var content = new ByteArrayContent(BuildRequestBody(request));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        httpRequest.Content = content;

        using var response = await SendAsync(httpRequest, request.Model.Trim(), ct).ConfigureAwait(false);
        using var reader = await OpenReaderAsync(response, ct).ConfigureAwait(false);

        var yielded = false;
        StringBuilder? otherLines = null;
        while (await ReadLineAsync(reader, ct).ConfigureAwait(false) is { } line)
        {
            var ev = OpenAiStreamParser.ParseLine(line);
            if (ev.Kind == OpenAiStreamEventKind.Done)
                break;
            if (ev.Kind == OpenAiStreamEventKind.Error)
                throw new ChatClientException($"OpenAI error: {ev.Text}");
            if (ev.Kind == OpenAiStreamEventKind.Delta)
            {
                yielded = true;
                yield return ev.Text!;
                continue;
            }

            if (!yielded && IsNonSseLine(line))
            {
                otherLines ??= new StringBuilder();
                if (otherLines.Length < MaxNonStreamBodyChars)
                    otherLines.Append(line).Append('\n');
            }
        }

        if (!yielded && otherLines is not null)
        {
            var body = OpenAiStreamParser.ParseCompleteBody(otherLines.ToString());
            if (body.Kind == OpenAiStreamEventKind.Error)
                throw new ChatClientException($"OpenAI error: {body.Text}");
            if (body.Kind == OpenAiStreamEventKind.Delta)
            {
                yielded = true;
                yield return body.Text!;
            }
        }

        // Without this the UI would show a blank answer with no explanation (typically a reasoning model that
        // spent its whole token budget thinking, or a content filter).
        if (!yielded)
        {
            throw new ChatClientException(
                "The model returned an empty answer. Try again, or raise Max tokens / lower Reasoning effort in Settings.");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "models", apiKey);
        using var response = await SendAsync(request, model: null, ct).ConfigureAwait(false);

        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeout.CancelAfter(HeadersTimeout);
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(bodyTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Listing models was cancelled.", ex, ct);
        }
        catch (OperationCanceledException ex)
        {
            throw new ChatClientException(NoResponseMessage(), null, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ChatClientException($"Could not reach OpenAI: {DescribeNetworkError(ex)}", null, ex);
        }

        return OpenAiStreamParser.ParseModelIds(body)
            ?? throw new ChatClientException("The server's model list could not be read. Check the base URL in Settings.");
    }

    /// <summary>Serializes the Chat Completions request body (no reflection; trim-safe).</summary>
    internal static byte[] BuildRequestBody(ChatRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model.Trim());
            w.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                w.WriteStartObject();
                w.WriteString("role", message.Role);
                w.WriteString("content", message.Content);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("stream", true);
            // OpenAI deprecated max_tokens (reasoning models reject it); most compatible servers only know max_tokens.
            w.WriteNumber(IsOpenAiHost(request.BaseUrl) ? "max_completion_tokens" : "max_tokens", request.MaxTokens);
            if (request.Temperature is { } temperature && double.IsFinite(temperature))
                w.WriteNumber("temperature", temperature);
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
                w.WriteString("reasoning_effort", request.ReasoningEffort.Trim());
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static bool IsOpenAiHost(string? baseUrl) =>
        Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
        && uri.Host.Equals(OpenAiHost, StringComparison.OrdinalIgnoreCase);

    internal static Uri BuildEndpoint(string? baseUrl, string path)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed + "/" + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ChatClientException("The OpenAI base URL isn't a valid http(s) address. Check it in Settings.");
        }
        return uri;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string? baseUrl, string path, string? apiKey)
    {
        var uri = BuildEndpoint(baseUrl, path);
        var key = (apiKey ?? "").Trim();
        // Keys pasted from documents can carry invisible characters that HTTP headers reject.
        if (key.Any(c => c is < '!' or > '~'))
            throw new ChatClientException("The OpenAI API key contains invalid characters. Paste it again in Settings.");

        var request = new HttpRequestMessage(method, uri);
        if (key.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    /// <summary>Sends the request and returns a successful response with its headers read (body not yet read).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeadersTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The request was cancelled.", ex, ct);
        }
        catch (OperationCanceledException ex)
        {
            throw new ChatClientException(NoResponseMessage(), null, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or NotSupportedException)
        {
            throw new ChatClientException($"Could not reach OpenAI: {DescribeNetworkError(ex)}", null, ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            throw await CreateErrorAsync(response, model, timeout.Token, ct).ConfigureAwait(false);
        }
    }

    private string NoResponseMessage() =>
        $"Could not reach OpenAI: no response within {Math.Max(1, Math.Round(HeadersTimeout.TotalSeconds))} seconds.";

    private static async Task<ChatClientException> CreateErrorAsync(
        HttpResponseMessage response, string? model, CancellationToken readToken, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(readToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The request was cancelled.", ex, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // The status code alone still makes a useful message.
        }

        var detail = OpenAiStreamParser.TryExtractErrorMessage(body) ?? DescribeNonJsonBody(body, response.ReasonPhrase);
        var message = code switch
        {
            401 => "OpenAI rejected the API key (401). Check it in Settings.",
            404 when model is not null => $"Model '{model}' was not found or you don't have access (404).",
            404 => $"OpenAI error 404: {detail.TrimEnd('.')}. Check the base URL in Settings.",
            429 => $"OpenAI rate limit or quota exceeded (429): {detail}",
            _ => $"OpenAI error {code}: {detail}",
        };
        return new ChatClientException(message, code);
    }

    private static string DescribeNonJsonBody(string? body, string? reasonPhrase)
    {
        var text = body?.Trim() ?? "";
        // HTML error pages from proxies/gateways are noise for the user; the reason phrase says enough.
        if (text.Length == 0 || text.StartsWith('<'))
            return string.IsNullOrWhiteSpace(reasonPhrase) ? "no details" : reasonPhrase.Trim();
        return text.Length <= 300 ? text : string.Concat(text.AsSpan(0, 300), "…");
    }

    private static async Task<StreamReader> OpenReaderAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The request was cancelled.", ex, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ChatClientException($"Could not reach OpenAI: {DescribeNetworkError(ex)}", null, ex);
        }
    }

    private static async ValueTask<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // Cancelling a socket read can surface as IOException/ObjectDisposedException; callers expect OCE.
            throw new OperationCanceledException("The answer was cancelled.", ex, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ChatClientException($"The connection to OpenAI was interrupted: {DescribeNetworkError(ex)}", null, ex);
        }
    }

    private static bool IsNonSseLine(string line) =>
        line.Length > 0
        && line[0] != ':'
        && !line.StartsWith("data:", StringComparison.Ordinal)
        && !line.StartsWith("event:", StringComparison.Ordinal)
        && !line.StartsWith("id:", StringComparison.Ordinal)
        && !line.StartsWith("retry:", StringComparison.Ordinal);

    private static string DescribeNetworkError(Exception ex)
    {
        // Messages like "The SSL connection could not be established, see inner exception." hide the real cause.
        var message = ex.Message;
        if (ex.InnerException is { } inner
            && (message.Contains("inner exception", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("An error occurred while sending the request", StringComparison.Ordinal)))
        {
            message = inner.Message;
        }
        return message.Trim();
    }
}
