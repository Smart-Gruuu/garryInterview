using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Abstractions;

public interface IPromptBuilder
{
    /// <summary>
    /// Builds the chat messages for one answer: a system prompt (role, rules, answer style, resume, job description,
    /// notes, custom instructions), up to settings.HistoryDepth previous Q&amp;A pairs from <paramref name="history"/>
    /// (oldest first, only completed ones without errors), then the new question with optional recent transcript context.
    /// </summary>
    IReadOnlyList<ChatMessage> Build(
        AppSettings settings,
        string question,
        IReadOnlyList<QaItem> history,
        string recentTranscript);

    /// <summary>Builds the full <see cref="ChatRequest"/> (model, token limit, temperature/reasoning handling).</summary>
    ChatRequest BuildRequest(AppSettings settings, IReadOnlyList<ChatMessage> messages);
}
