namespace InterviewAssist.Core.Models;

/// <summary>An OpenAI chat message. Role is "system", "user" or "assistant".</summary>
public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(
    string BaseUrl,
    string ApiKey,
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens,
    double? Temperature,
    string? ReasoningEffort);

/// <summary>A failure talking to the chat API, with a message that is safe to show to the user.</summary>
public sealed class ChatClientException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}
