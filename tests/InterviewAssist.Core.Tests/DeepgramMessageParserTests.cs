using System.Globalization;
using InterviewAssist.Core.Models;
using InterviewAssist.Core.Transcription;

namespace InterviewAssist.Core.Tests;

public class DeepgramMessageParserTests
{
    private static string Results(
        string transcript,
        bool isFinal,
        bool speechFinal = false,
        bool? fromFinalize = null,
        double start = 1.5,
        double duration = 2.25)
    {
        var fromFinalizeJson = fromFinalize is { } value ? $",\"from_finalize\":{(value ? "true" : "false")}" : "";
        return $$$"""
            {"type":"Results","channel_index":[0,1],"duration":{{{duration.ToString(CultureInfo.InvariantCulture)}}},"start":{{{start.ToString(CultureInfo.InvariantCulture)}}},
             "is_final":{{{(isFinal ? "true" : "false")}}},"speech_final":{{{(speechFinal ? "true" : "false")}}}{{{fromFinalizeJson}}},
             "channel":{"alternatives":[{"transcript":"{{{transcript}}}","confidence":0.98,"words":[]}]},
             "metadata":{"request_id":"abc","model_info":{"name":"general-nova-3"} } }
            """;
    }

    [Fact]
    public void InterimResult_IsParsed()
    {
        var message = DeepgramMessageParser.Parse(Results("tell me about", isFinal: false), streamId: 3);

        Assert.Equal(DeepgramMessageKind.Results, message.Kind);
        Assert.False(message.FromFinalize);
        var result = Assert.IsType<TranscriptResult>(message.Result);
        Assert.Equal(3, result.StreamId);
        Assert.Equal("tell me about", result.Text);
        Assert.False(result.IsFinal);
        Assert.False(result.SpeechFinal);
        Assert.False(result.FromFinalize);
        Assert.Equal(1.5, result.Start);
        Assert.Equal(2.25, result.Duration);
        Assert.Equal(3.75, result.End);
    }

    [Fact]
    public void FinalResult_WithSpeechFinal_IsParsed()
    {
        var message = DeepgramMessageParser.Parse(
            Results("Tell me about yourself.", isFinal: true, speechFinal: true, fromFinalize: false), streamId: 1);

        var result = Assert.IsType<TranscriptResult>(message.Result);
        Assert.True(result.IsFinal);
        Assert.True(result.SpeechFinal);
        Assert.False(result.FromFinalize);
        Assert.Equal("Tell me about yourself.", result.Text);
    }

    [Fact]
    public void FromFinalizeResult_SetsFlagOnMessageAndResult()
    {
        var message = DeepgramMessageParser.Parse(
            Results("And why this company?", isFinal: true, fromFinalize: true), streamId: 2);

        Assert.Equal(DeepgramMessageKind.Results, message.Kind);
        Assert.True(message.FromFinalize);
        Assert.True(message.Result!.FromFinalize);
        Assert.True(message.Result.IsFinal);
    }

    [Fact]
    public void EmptyTranscript_StillReturnsResultWithEmptyText()
    {
        var message = DeepgramMessageParser.Parse(Results("   ", isFinal: true, fromFinalize: true), streamId: 1);

        Assert.Equal(DeepgramMessageKind.Results, message.Kind);
        Assert.True(message.FromFinalize);
        Assert.Equal("", message.Result!.Text);
    }

    [Fact]
    public void Results_WithMissingFields_UsesDefaults()
    {
        var message = DeepgramMessageParser.Parse("""{"type":"Results"}""", streamId: 4);

        Assert.Equal(DeepgramMessageKind.Results, message.Kind);
        var result = message.Result!;
        Assert.Equal(4, result.StreamId);
        Assert.Equal("", result.Text);
        Assert.False(result.IsFinal);
        Assert.False(result.FromFinalize);
        Assert.Equal(0, result.Start);
        Assert.Equal(0, result.Duration);
    }

    [Fact]
    public void Results_WithWrongFieldTypes_IsTolerated()
    {
        const string json = """
            {"type":"Results","start":"soon","duration":null,"is_final":"yes",
             "channel":{"alternatives":"none"}}
            """;

        var message = DeepgramMessageParser.Parse(json, streamId: 1);

        Assert.Equal(DeepgramMessageKind.Results, message.Kind);
        Assert.Equal("", message.Result!.Text);
        Assert.False(message.Result.IsFinal);
        Assert.Equal(0, message.Result.Start);
    }

    [Fact]
    public void UtteranceEnd_IsRecognized()
    {
        var message = DeepgramMessageParser.Parse(
            """{"type":"UtteranceEnd","channel":[0,1],"last_word_end":2.395}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.UtteranceEnd, message.Kind);
        Assert.Null(message.Result);
    }

    [Fact]
    public void Metadata_IsRecognized()
    {
        var message = DeepgramMessageParser.Parse(
            """{"type":"Metadata","request_id":"x","duration":12.5,"channels":1}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.Metadata, message.Kind);
    }

    [Fact]
    public void SpeechStarted_IsRecognized()
    {
        var message = DeepgramMessageParser.Parse(
            """{"type":"SpeechStarted","channel":[0,1],"timestamp":9.54}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.SpeechStarted, message.Kind);
    }

    [Fact]
    public void ErrorType_UsesDescription()
    {
        var message = DeepgramMessageParser.Parse(
            """{"type":"Error","description":"Failed to decode audio.","message":"DATA-0000","variant":"x"}""",
            streamId: 1);

        Assert.Equal(DeepgramMessageKind.Error, message.Kind);
        Assert.Equal("Failed to decode audio.", message.ErrorMessage);
        Assert.Null(message.Result);
    }

    [Fact]
    public void ErrorType_WithoutDescription_FallsBackToMessage()
    {
        var message = DeepgramMessageParser.Parse("""{"type":"Error","message":"Something broke"}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.Error, message.Kind);
        Assert.Equal("Something broke", message.ErrorMessage);
    }

    [Fact]
    public void ErrorType_WithoutAnyText_HasDefaultMessage()
    {
        var message = DeepgramMessageParser.Parse("""{"type":"Error"}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.Error, message.Kind);
        Assert.False(string.IsNullOrWhiteSpace(message.ErrorMessage));
    }

    [Fact]
    public void LegacyErrMsg_IsAnError()
    {
        var message = DeepgramMessageParser.Parse(
            """{"err_code":"INVALID_AUTH","err_msg":"Invalid credentials.","request_id":"r1"}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.Error, message.Kind);
        Assert.Equal("Invalid credentials.", message.ErrorMessage);
    }

    [Fact]
    public void UntypedMessage_WithDescription_IsAnError()
    {
        var message = DeepgramMessageParser.Parse("""{"description":"Too many requests"}""", streamId: 1);

        Assert.Equal(DeepgramMessageKind.Error, message.Kind);
        Assert.Equal("Too many requests", message.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"Results\"")]
    [InlineData("[1,2,3]")]
    [InlineData("\"Results\"")]
    [InlineData("null")]
    [InlineData("""{"type":"SomethingNew","value":1}""")]
    [InlineData("""{"type":42}""")]
    public void GarbageOrUnknown_ReturnsUnknown(string json)
    {
        var message = DeepgramMessageParser.Parse(json, streamId: 1);

        Assert.Equal(DeepgramMessageKind.Unknown, message.Kind);
        Assert.Null(message.Result);
        Assert.Null(message.ErrorMessage);
    }
}
