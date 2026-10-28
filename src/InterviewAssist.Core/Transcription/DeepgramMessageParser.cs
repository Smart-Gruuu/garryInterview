using System.Text.Json;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Transcription;

internal enum DeepgramMessageKind
{
    Results,
    UtteranceEnd,
    Metadata,
    SpeechStarted,
    Error,
    Unknown,
}

/// <summary>One parsed server message from the Deepgram live API.</summary>
/// <param name="Result">Set for <see cref="DeepgramMessageKind.Results"/> (Text is trimmed and may be empty).</param>
/// <param name="FromFinalize">True when this result answers a Finalize request.</param>
/// <param name="ErrorMessage">Set for <see cref="DeepgramMessageKind.Error"/>.</param>
internal sealed record DeepgramMessage(
    DeepgramMessageKind Kind,
    TranscriptResult? Result = null,
    bool FromFinalize = false,
    string? ErrorMessage = null);

/// <summary>Pure, tolerant parser for Deepgram live-streaming JSON messages. Never throws.</summary>
internal static class DeepgramMessageParser
{
    private const string DefaultErrorMessage = "Deepgram reported an error.";

    private static readonly DeepgramMessage UnknownMessage = new(DeepgramMessageKind.Unknown);
    private static readonly DeepgramMessage UtteranceEndMessage = new(DeepgramMessageKind.UtteranceEnd);
    private static readonly DeepgramMessage MetadataMessage = new(DeepgramMessageKind.Metadata);
    private static readonly DeepgramMessage SpeechStartedMessage = new(DeepgramMessageKind.SpeechStarted);

    public static DeepgramMessage Parse(string json, int streamId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return UnknownMessage;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UnknownMessage;
            }

            var type = GetString(root, "type");
            if (IsType(type, "Results"))
            {
                return ParseResults(root, streamId);
            }

            if (IsType(type, "UtteranceEnd"))
            {
                return UtteranceEndMessage;
            }

            if (IsType(type, "Metadata"))
            {
                return MetadataMessage;
            }

            if (IsType(type, "SpeechStarted"))
            {
                return SpeechStartedMessage;
            }

            // Newer API errors: {"type":"Error","description":...,"message":...}; older ones: {"err_code":...,"err_msg":...}.
            var description = GetNonEmptyString(root, "description") ?? GetNonEmptyString(root, "err_msg");
            if (description is not null || IsType(type, "Error"))
            {
                var message = description ?? GetNonEmptyString(root, "message") ?? DefaultErrorMessage;
                return new DeepgramMessage(DeepgramMessageKind.Error, ErrorMessage: message.Trim());
            }

            return UnknownMessage;
        }
        catch (JsonException)
        {
            return UnknownMessage;
        }
    }

    private static DeepgramMessage ParseResults(JsonElement root, int streamId)
    {
        var text = "";
        if (root.TryGetProperty("channel", out var channel)
            && channel.ValueKind == JsonValueKind.Object
            && channel.TryGetProperty("alternatives", out var alternatives)
            && alternatives.ValueKind == JsonValueKind.Array
            && alternatives.GetArrayLength() > 0)
        {
            var first = alternatives[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                text = GetString(first, "transcript") ?? "";
            }
        }

        var fromFinalize = GetBool(root, "from_finalize");
        var result = new TranscriptResult(
            streamId,
            text.Trim(),
            IsFinal: GetBool(root, "is_final"),
            SpeechFinal: GetBool(root, "speech_final"),
            FromFinalize: fromFinalize,
            Start: GetDouble(root, "start"),
            Duration: GetDouble(root, "duration"));

        return new DeepgramMessage(DeepgramMessageKind.Results, result, fromFinalize);
    }

    private static bool IsType(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNonEmptyString(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? number
            : 0;
}
