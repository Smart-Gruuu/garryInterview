using System.Text;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Chat;

/// <summary>
/// Turns the settings, the interview profile and the question into chat messages. Stateless and deterministic:
/// the system prompt depends only on the settings, so it stays byte-identical across questions and benefits from
/// the provider's prompt caching (lower latency on every question after the first).
/// </summary>
public sealed class PromptBuilder : IPromptBuilder
{
    public const int MaxSectionChars = 12_000;
    public const int MinAnswerTokenLimit = 100;
    public const int MaxAnswerTokenLimit = 4000;

    /// <summary>Extra completion tokens for reasoning models, whose hidden thinking counts against the limit.</summary>
    public const int ReasoningTokenHeadroom = 2000;

    public const int MaxHistoryDepth = 20;
    public const string DefaultReasoningEffort = "low";

    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private const double DefaultTemperature = 0.4;

    // The candidate's own voice and style rules (their long-standing prompt, kept close to their wording).
    internal const string Persona =
        "You are a senior-level job candidate in a spoken interview. Answer everything in natural, conversational American English — "
        + "like you're actually talking to someone face to face. Use casual words, slang, short sentences, and occasional laugh sounds "
        + "(like \"haha\" or \"you know\"). Keep it simple and easy to read out loud. For non-technical questions, give short, real-life "
        + "answers — no deep explanations. For technical questions, explain in plain everyday language — not systematic, not broken into "
        + "categories, easy to understand, with no code. No code, no complex syntax, no hard-to-read details. If the question isn't "
        + "covered in the resume, ignore the resume and answer confidently as a senior. Add light humor when it fits naturally. "
        + "Keep each response under about 1 minute of speaking time.";

    // Mechanics of the live setting; they don't change the voice above.
    private const string HowItWorks =
        """
        How this works:
        - Each question comes from live speech-to-text, so it may have recognition errors, filler words or bits of earlier talk. Work out what the interviewer meant; if they asked several things, answer each in order.
        - Reply with only the words the candidate will say, in first person. No intro like "Great question", no notes or tips to the candidate, no "As an AI".
        - When the resume has a fitting example, use its real companies, projects and results.
        - If it isn't a question (small talk, a comment), just give a short natural reply.
        """;

    // Distilled from the interview-advice articles the candidate relies on (paraphrased, not quoted).
    internal const string CoachingNotes =
        """
        - Tell me about yourself: now (current role plus a recent win), then (how you got here), next (why this job fits). Tailor it to the job.
        - Why this role, or why you: point to one or two specific needs in the job description and prove each with a real win.
        - What you're looking for: skills you want to use for them and room to grow here. Never pay, perks, or knocking your current job.
        - What you know about us: what they do, what sets them apart, and how it links to this role. Use only facts you're sure of; never invent company details.
        - Sell results, not duties: your one or two biggest wins, with numbers or real outcomes. Name only the tools this job uses.
        - Stories, not flat STAR recitals: quick setup, the tension and stakes, what you did and how it turned out, then a one-line lesson.
        - Show how you think: why you picked that path and how you handled the unclear parts. Talk like a teammate already working their problem.
        - Weakness: a genuine gap plus the steps you're taking; never claim none. Setbacks, firings or rough bosses: share the lesson, never blame anyone.
        - Don't raise salary first. If asked, stay flexible and steer back to fit and the work; name a range only if pushed.
        - When asked for questions, give 2–3 teammate-style ones from the job description: the hire's biggest problem to fix, what great looks like at 90 days, where work stalls today.
        - Skip questions about a typical day, promotion timing, turnover or vague culture. Ask about learning instead, and close by asking if they have any doubts about you.
        - No buzzwords or hollow lines like "I love tech." Sound sure of yourself without bragging.
        """;

    internal const string InterviewTypeHeading = "## This interview: ";
    internal const string CoachingHeading = "## Interview coaching notes";
    internal const string RoleHeading = "## Role";
    internal const string ResumeHeading = "## Candidate resume";
    internal const string JobDescriptionHeading = "## Job description";
    internal const string NotesHeading = "## Candidate's notes";
    internal const string CustomInstructionsHeading = "## Extra instructions from the candidate (these override everything above)";

    internal const string TranscriptPrefix = "Recent conversation (live transcript, may be imperfect):\n";
    internal const string QuestionPrefix = "Interviewer's question: ";
    internal const string HistoryQuestionPrefix = "Interviewer: ";

    public IReadOnlyList<ChatMessage> Build(
        AppSettings settings,
        string question,
        IReadOnlyList<QaItem> history,
        string recentTranscript)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);

        var messages = new List<ChatMessage> { new("system", BuildSystemPrompt(settings)) };

        foreach (var item in SelectHistory(history, settings.HistoryDepth))
        {
            messages.Add(new ChatMessage("user", HistoryQuestionPrefix + Clean(item.Question)));
            messages.Add(new ChatMessage("assistant", item.Answer.Trim()));
        }

        var transcript = Clean(recentTranscript);
        var final = transcript.Length > 0
            ? TranscriptPrefix + transcript + "\n\n" + QuestionPrefix + Clean(question)
            : QuestionPrefix + Clean(question);
        messages.Add(new ChatMessage("user", final));
        return messages;
    }

    public ChatRequest BuildRequest(AppSettings settings, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);

        var model = settings.OpenAiModel?.Trim() is { Length: > 0 } m ? m : SettingOptions.SuggestedOpenAiModels[0];
        var baseUrl = settings.OpenAiBaseUrl?.Trim() is { Length: > 0 } b ? b : DefaultBaseUrl;
        var apiKey = settings.OpenAiApiKey?.Trim() ?? "";
        var maxTokens = Math.Clamp(settings.MaxAnswerTokens, MinAnswerTokenLimit, MaxAnswerTokenLimit);

        if (IsReasoningModel(model))
        {
            var effort = settings.ReasoningEffort?.Trim().ToLowerInvariant() is { Length: > 0 } e ? e : DefaultReasoningEffort;
            return new ChatRequest(baseUrl, apiKey, model, messages, maxTokens + ReasoningTokenHeadroom, null, effort);
        }

        var temperature = double.IsFinite(settings.Temperature) ? Math.Clamp(settings.Temperature, 0.0, 2.0) : DefaultTemperature;
        return new ChatRequest(baseUrl, apiKey, model, messages, maxTokens, temperature, null);
    }

    /// <summary>
    /// True for OpenAI reasoning models (o1, o3, o4, gpt-5 families), which reject <c>temperature</c> and accept
    /// <c>reasoning_effort</c>. The "-chat" variants (e.g. gpt-5-chat-latest) are regular chat models.
    /// A vendor prefix such as "openai/" (OpenRouter-style ids) is ignored.
    /// </summary>
    public static bool IsReasoningModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var id = model.Trim();
        id = id[(id.LastIndexOf('/') + 1)..];
        if (id.Contains("-chat", StringComparison.OrdinalIgnoreCase))
            return false;

        return id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Persona → mechanics and answer style → this round's adjustments → coaching notes → profile → the candidate's
    /// own extra instructions (last, so they win).
    /// </summary>
    internal static string BuildSystemPrompt(AppSettings settings)
    {
        var sb = new StringBuilder(4096);
        sb.Append(Persona).Append("\n\n").Append(HowItWorks.ReplaceLineEndings("\n")).Append('\n');
        sb.Append("- ").Append(LengthRule(settings.AnswerLength))
          .Append(' ').Append(FormatRule(settings.AnswerFormat));
        if (LanguageRule(settings.AnswerLanguage) is { Length: > 0 } language)
            sb.Append(' ').Append(language);

        var interviewType = InterviewTypes.Find(settings.InterviewType);
        AppendSection(sb, InterviewTypeHeading + interviewType.Name + " (adjusts the style above for this round)", interviewType.Guidance);
        AppendSection(sb, CoachingHeading, CoachingNotes);
        AppendSection(sb, RoleHeading, RoleText(settings.RoleTitle, settings.CompanyName));
        AppendSection(sb, ResumeHeading, settings.Resume);
        AppendSection(sb, JobDescriptionHeading, settings.JobDescription);
        AppendSection(sb, NotesHeading, settings.ExtraNotes);
        AppendSection(sb, CustomInstructionsHeading, settings.CustomInstructions);
        return sb.ToString();
    }

    internal static string LengthRule(string? length) => Normalize(length) switch
    {
        "short" => "Keep it extra short: about 20–30 seconds spoken (under ~70 words).",
        "detailed" => "You can go longer than a minute here — up to about 2 minutes (250–300 words) when the question needs it.",
        _ => "Stay under about a minute spoken (roughly 100–150 words).",
    };

    internal static string FormatRule(string? format) => Normalize(format) switch
    {
        "hybrid" => "Start with one or two sentences in **bold** that directly answer, then 3–5 short talking points in the same casual voice.",
        "bullets" => "Give a one-line answer, then 3–6 short talking points in the same casual voice.",
        _ => "Just talk: natural spoken sentences, no bullets, headings or bold.",
    };

    /// <summary>"" when no language is set — the persona already asks for American English.</summary>
    internal static string LanguageRule(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "" : $"Answer in {language.Trim()}.";

    /// <summary>Completed Q&amp;A items (answer present, no error, not streaming), the newest <paramref name="depth"/>, oldest first.</summary>
    internal static List<QaItem> SelectHistory(IReadOnlyList<QaItem> history, int depth)
    {
        depth = Math.Clamp(depth, 0, MaxHistoryDepth);
        var selected = new List<QaItem>(depth);
        for (var i = history.Count - 1; i >= 0 && selected.Count < depth; i--)
        {
            var item = history[i];
            if (item is null || item.IsStreaming || item.HasError
                || string.IsNullOrWhiteSpace(item.Answer) || string.IsNullOrWhiteSpace(item.Question))
            {
                continue;
            }
            selected.Add(item);
        }
        selected.Reverse();
        return selected;
    }

    /// <summary>Trims and normalizes line endings, then caps the length (keeping the start, where resumes/JDs front-load).</summary>
    internal static string Truncate(string? text)
    {
        var clean = Clean(text);
        if (clean.Length <= MaxSectionChars)
            return clean;

        var cut = MaxSectionChars;
        if (char.IsHighSurrogate(clean[cut - 1]))
            cut--;
        return clean[..cut].TrimEnd() + "…";
    }

    private static void AppendSection(StringBuilder sb, string heading, string? content)
    {
        var text = Truncate(content);
        if (text.Length == 0)
            return;
        sb.Append("\n\n").Append(heading).Append('\n').Append(text);
    }

    private static string RoleText(string? roleTitle, string? companyName)
    {
        var title = Clean(roleTitle);
        var company = Clean(companyName);
        if (title.Length > 0 && company.Length > 0)
            return $"Position: {title}\nCompany: {company}";
        if (title.Length > 0)
            return $"Position: {title}";
        return company.Length > 0 ? $"Company: {company}" : "";
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : text.ReplaceLineEndings("\n").Trim();

    private static string Normalize(string? option) => option?.Trim().ToLowerInvariant() ?? "";
}
