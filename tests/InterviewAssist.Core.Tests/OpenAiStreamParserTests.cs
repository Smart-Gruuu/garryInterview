using InterviewAssist.Core.Chat;

namespace InterviewAssist.Core.Tests;

public class OpenAiStreamParserTests
{
    [Fact]
    public void Delta_content_is_returned()
    {
        var ev = OpenAiStreamParser.ParseLine(
            """data: {"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}""");

        Assert.Equal(OpenAiStreamEventKind.Delta, ev.Kind);
        Assert.Equal("Hello", ev.Text);
    }

    [Fact]
    public void Data_without_space_after_colon_is_accepted()
    {
        var ev = OpenAiStreamParser.ParseLine("""data:{"choices":[{"delta":{"content":" world"}}]}""");

        Assert.Equal(OpenAiStreamEventKind.Delta, ev.Kind);
        Assert.Equal(" world", ev.Text);
    }

    [Fact]
    public void Escaped_and_unicode_content_is_decoded()
    {
        var ev = OpenAiStreamParser.ParseLine("""data: {"choices":[{"delta":{"content":"line1\nline2 \"q\" caf\u00e9 ```"}}]}""");

        Assert.Equal("line1\nline2 \"q\" café ```", ev.Text);
    }

    [Fact]
    public void Done_marker_is_recognised()
    {
        Assert.Equal(OpenAiStreamEventKind.Done, OpenAiStreamParser.ParseLine("data: [DONE]").Kind);
        Assert.Equal(OpenAiStreamEventKind.Done, OpenAiStreamParser.ParseLine("data:[DONE]  ").Kind);
    }

    [Theory]
    [InlineData("""data: {"choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""")]
    [InlineData("""data: {"choices":[{"index":0,"delta":{"content":null},"finish_reason":null}]}""")]
    [InlineData("""data: {"choices":[{"index":0,"delta":{"content":""}}]}""")]
    [InlineData("""data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""")]
    [InlineData("""data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5}}""")]
    [InlineData("""data: {"choices":[{"delta":{"reasoning_content":"thinking..."}}]}""")]
    public void Chunks_without_text_are_ignored(string line)
    {
        var ev = OpenAiStreamParser.ParseLine(line);

        Assert.Equal(OpenAiStreamEventKind.Ignore, ev.Kind);
        Assert.Null(ev.Text);
    }

    [Fact]
    public void Null_error_field_is_not_an_error()
    {
        var ev = OpenAiStreamParser.ParseLine("""data: {"choices":[{"delta":{"content":"x"}}],"error":null}""");

        Assert.Equal(OpenAiStreamEventKind.Delta, ev.Kind);
        Assert.Equal("x", ev.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(": OPENROUTER PROCESSING")]
    [InlineData("event: message")]
    [InlineData("id: 42")]
    [InlineData("retry: 1000")]
    [InlineData("data: ")]
    [InlineData("data: {not json")]
    [InlineData("data: \"just a string\"")]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"no data prefix\"}}]}")]
    public void Blank_comment_and_other_lines_are_ignored(string? line)
    {
        Assert.Equal(OpenAiStreamEventKind.Ignore, OpenAiStreamParser.ParseLine(line).Kind);
    }

    [Fact]
    public void Error_object_is_reported()
    {
        var ev = OpenAiStreamParser.ParseLine(
            """data: {"error":{"message":"The server had an error while processing your request.","type":"server_error"}}""");

        Assert.Equal(OpenAiStreamEventKind.Error, ev.Kind);
        Assert.Equal("The server had an error while processing your request.", ev.Text);
    }

    [Fact]
    public void Error_string_is_reported()
    {
        var ev = OpenAiStreamParser.ParseLine("""data: {"error":"model overloaded"}""");

        Assert.Equal(OpenAiStreamEventKind.Error, ev.Kind);
        Assert.Equal("model overloaded", ev.Text);
    }

    [Fact]
    public void Error_without_message_still_reports_an_error()
    {
        var ev = OpenAiStreamParser.ParseLine("""data: {"error":{"code":"server_error"}}""");

        Assert.Equal(OpenAiStreamEventKind.Error, ev.Kind);
        Assert.Equal("server_error", ev.Text);
    }

    [Fact]
    public void Complete_body_yields_message_content()
    {
        var ev = OpenAiStreamParser.ParseCompleteBody(
            """
            {
              "id": "c1",
              "choices": [ { "index": 0, "message": { "role": "assistant", "content": "Full answer" }, "finish_reason": "stop" } ]
            }
            """);

        Assert.Equal(OpenAiStreamEventKind.Delta, ev.Kind);
        Assert.Equal("Full answer", ev.Text);
    }

    [Fact]
    public void Complete_body_with_error_is_reported()
    {
        var ev = OpenAiStreamParser.ParseCompleteBody("""{"error":{"message":"bad things"}}""");

        Assert.Equal(OpenAiStreamEventKind.Error, ev.Kind);
        Assert.Equal("bad things", ev.Text);
    }

    [Theory]
    [InlineData("""{"error":{"message":"Incorrect API key provided"}}""", "Incorrect API key provided")]
    [InlineData("""{"error":"quota"}""", "quota")]
    [InlineData("""{"message":"not found"}""", "not found")]
    [InlineData("""{"detail":"Unauthorized"}""", "Unauthorized")]
    [InlineData("""{"foo":1}""", null)]
    [InlineData("<html>Bad gateway</html>", null)]
    [InlineData("", null)]
    public void Error_message_is_extracted_from_error_bodies(string body, string? expected)
    {
        Assert.Equal(expected, OpenAiStreamParser.TryExtractErrorMessage(body));
    }

    [Fact]
    public void Model_ids_are_sorted_ignoring_case_and_deduplicated()
    {
        var ids = OpenAiStreamParser.ParseModelIds(
            """{"object":"list","data":[{"id":"gpt-4o"},{"id":"GPT-4.1"},{"id":"babbage-002"},{"id":"gpt-4o"},{"id":""},{"name":"x"}]}""");

        Assert.Equal(["babbage-002", "GPT-4.1", "gpt-4o"], ids);
    }

    [Theory]
    [InlineData("""{"object":"list"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Model_list_that_is_not_a_list_returns_null(string body)
    {
        Assert.Null(OpenAiStreamParser.ParseModelIds(body));
    }
}
