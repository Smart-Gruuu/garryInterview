using System.Text.Json;

namespace InterviewAssist.Core.Chat;

internal enum OpenAiStreamEventKind
{
    /// <summary>A piece of answer text (<see cref="OpenAiStreamEvent.Text"/> is non-empty).</summary>
    Delta,

    /// <summary><c>data: [DONE]</c> — the stream is complete.</summary>
    Done,

    /// <summary>The server reported an error; <see cref="OpenAiStreamEvent.Text"/> is its message.</summary>
    Error,

    /// <summary>Nothing useful: blank line, SSE comment/field, role-only or finish chunk, malformed JSON.</summary>
    Ignore,
}

internal readonly record struct OpenAiStreamEvent(OpenAiStreamEventKind Kind, string? Text = null)
{
    public static readonly OpenAiStreamEvent Ignored = new(OpenAiStreamEventKind.Ignore);
    public static readonly OpenAiStreamEvent Done = new(OpenAiStreamEventKind.Done);
}

/// <summary>Pure parsing helpers for OpenAI-compatible Chat Completions responses.</summary>
internal static class OpenAiStreamParser
{
    private const string DataPrefix = "data:";

    /// <summary>Parses one line of a Server-Sent Events stream.</summary>
    public static OpenAiStreamEvent ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(DataPrefix, StringComparison.Ordinal))
            return OpenAiStreamEvent.Ignored;

        var payload = line.AsSpan(DataPrefix.Length).Trim();
        if (payload.SequenceEqual("[DONE]"))
            return OpenAiStreamEvent.Done;
        if (payload.IsEmpty || payload[0] != '{')
            return OpenAiStreamEvent.Ignored;

        try
        {
            using var doc = JsonDocument.Parse(payload.ToString());
            var root = doc.RootElement;

            if (TryGetErrorMessage(root, out var error))
                return new OpenAiStreamEvent(OpenAiStreamEventKind.Error, error);

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    return new OpenAiStreamEvent(OpenAiStreamEventKind.Delta, text);
            }
        }
        catch (JsonException)
        {
            // A malformed chunk is skipped; the rest of the stream is still usable.
        }

        return OpenAiStreamEvent.Ignored;
    }

    /// <summary>
    /// Parses a complete (non-streamed) JSON body, for OpenAI-compatible servers that ignore <c>stream: true</c>
    /// or report an error with a 2xx status. Returns a Delta with the whole answer, an Error, or Ignore.
    /// </summary>
    public static OpenAiStreamEvent ParseCompleteBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (TryGetErrorMessage(root, out var error))
                return new OpenAiStreamEvent(OpenAiStreamEventKind.Error, error);

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    return new OpenAiStreamEvent(OpenAiStreamEventKind.Delta, text);
            }
        }
        catch (JsonException)
        {
        }

        return OpenAiStreamEvent.Ignored;
    }

    /// <summary>
    /// Extracts a human-readable message from an error body: <c>{"error":{"message":…}}</c> (OpenAI),
    /// <c>{"error":"…"}</c>, <c>{"message":"…"}</c> or <c>{"detail":"…"}</c> (other compatible servers).
    /// Returns null when the body isn't JSON or has none of these.
    /// </summary>
    public static string? TryExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (TryGetErrorMessage(root, out var error))
                return error;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in (ReadOnlySpan<string>)["message", "detail"])
                {
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString()!.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>Parses a <c>GET /models</c> body into ids sorted ordinal-ignore-case. Null when it isn't a model list.</summary>
    public static IReadOnlyList<string>? ParseModelIds(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    ids.Add(id.GetString()!);
                }
            }
            return [.. ids];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetErrorMessage(JsonElement root, out string message)
    {
        message = "";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
            return false;

        switch (error.ValueKind)
        {
            case JsonValueKind.Object:
                if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(m.GetString()))
                {
                    message = m.GetString()!.Trim();
                }
                else if (error.TryGetProperty("code", out var code) && code.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    message = code.ToString();
                }
                else
                {
                    message = "unknown error";
                }
                return true;
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(error.GetString()):
                message = error.GetString()!.Trim();
                return true;
            default:
                // "error": null appears in some successful chunks from compatible servers.
                return false;
        }
    }
}
