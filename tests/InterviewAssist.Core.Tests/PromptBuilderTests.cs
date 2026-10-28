using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Tests;

public class PromptBuilderTests
{
    private readonly PromptBuilder _builder = new();

    private static AppSettings EmptyProfile() => new()
    {
        RoleTitle = "",
        CompanyName = "",
        Resume = "",
        JobDescription = "",
        ExtraNotes = "",
        CustomInstructions = "",
        HistoryDepth = 4,
    };

    private static QaItem Qa(string question, string answer, bool streaming = false, string error = "") => new()
    {
        Question = question,
        Answer = answer,
        IsStreaming = streaming,
        ErrorMessage = error,
    };

    private string System(AppSettings settings) =>
        _builder.Build(settings, "Tell me about yourself.", [], "")[0].Content;

    // ---- system prompt ----

    [Fact]
    public void System_prompt_starts_with_the_candidate_persona()
    {
        var messages = _builder.Build(EmptyProfile(), "Why this role?", [], "");

        Assert.Equal("system", messages[0].Role);
        var system = messages[0].Content;
        Assert.StartsWith("You are a senior-level job candidate in a spoken interview.", system);
        Assert.Contains("conversational American English", system);
        Assert.Contains("\"haha\" or \"you know\"", system);
        Assert.Contains("with no code", system);
        Assert.Contains("ignore the resume and answer confidently as a senior", system);
        Assert.Contains("light humor", system);
        Assert.Contains("under about 1 minute of speaking time", system);
        Assert.Contains("speech-to-text", system);
        Assert.Contains("answer each in order", system);
        Assert.Contains("in first person", system);
        Assert.Contains("\"As an AI\"", system);
        Assert.Contains("isn't a question", system);
        Assert.DoesNotContain("ready!", system);
        Assert.DoesNotContain("\r", system);
    }

    [Fact]
    public void Coaching_notes_are_always_included()
    {
        var system = System(EmptyProfile());

        Assert.Contains(PromptBuilder.CoachingHeading + "\n- ", system);
        Assert.Contains("Tell me about yourself", system);
    }

    [Fact]
    public void System_prompt_is_deterministic()
    {
        var settings = EmptyProfile();
        settings.Resume = "Ten years of C#.";
        settings.JobDescription = "Senior .NET engineer.";

        var first = _builder.Build(settings, "Q1", [], "")[0].Content;
        var second = _builder.Build(settings, "A different question", [Qa("Q1", "A1")], "some transcript")[0].Content;

        Assert.Equal(first, second);
    }

    private const string ShortRule = "Keep it extra short: about 20–30 seconds spoken (under ~70 words).";
    private const string MediumRule = "Stay under about a minute spoken (roughly 100–150 words).";
    private const string DetailedRule = "You can go longer than a minute here — up to about 2 minutes (250–300 words) when the question needs it.";
    private const string ScriptRule = "Just talk: natural spoken sentences, no bullets, headings or bold.";

    [Theory]
    [InlineData("Short", ShortRule)]
    [InlineData("Medium", MediumRule)]
    [InlineData("Detailed", DetailedRule)]
    [InlineData("detailed", DetailedRule)]
    [InlineData("unknown", MediumRule)]
    [InlineData("", MediumRule)]
    public void Length_rule_follows_the_setting(string length, string expected)
    {
        var settings = EmptyProfile();
        settings.AnswerLength = length;

        Assert.Contains(expected, System(settings));
    }

    [Theory]
    [InlineData("Script", ScriptRule)]
    [InlineData("Hybrid", "Start with one or two sentences in **bold** that directly answer, then 3–5 short talking points in the same casual voice.")]
    [InlineData("Bullets", "Give a one-line answer, then 3–6 short talking points in the same casual voice.")]
    [InlineData("nonsense", ScriptRule)]
    [InlineData("", ScriptRule)]
    public void Format_rule_follows_the_setting(string format, string expected)
    {
        var settings = EmptyProfile();
        settings.AnswerFormat = format;

        Assert.Contains(expected, System(settings));
    }

    [Fact]
    public void Default_settings_give_conversational_one_minute_answers()
    {
        var system = System(new AppSettings());

        Assert.Contains($"\n- {MediumRule} {ScriptRule}", system);
    }

    [Fact]
    public void No_language_rule_when_unset()
    {
        var settings = EmptyProfile();
        settings.AnswerLanguage = "  ";

        Assert.DoesNotContain("Answer in ", System(settings));
    }

    [Fact]
    public void Language_rule_uses_the_configured_language()
    {
        var settings = EmptyProfile();
        settings.AnswerLanguage = " Spanish ";

        Assert.Contains("Answer in Spanish.", System(settings));
    }

    [Fact]
    public void Style_rules_share_one_line()
    {
        var settings = EmptyProfile();
        settings.AnswerLength = "Short";
        settings.AnswerFormat = "Script";
        settings.AnswerLanguage = "German";

        Assert.Contains($"\n- {ShortRule} {ScriptRule} Answer in German.\n", System(settings));
    }

    // ---- interview type ----

    [Fact]
    public void Unset_interview_type_uses_general()
    {
        var settings = EmptyProfile();
        settings.InterviewType = "";

        Assert.Contains(PromptBuilder.InterviewTypeHeading + InterviewTypes.DefaultName + " ", System(settings));
    }

    [Fact]
    public void Every_interview_type_adds_its_guidance_before_the_coaching_notes()
    {
        foreach (var type in InterviewTypes.All)
        {
            var settings = EmptyProfile();
            settings.InterviewType = type.Name;

            var system = System(settings);

            var heading = system.IndexOf(PromptBuilder.InterviewTypeHeading + type.Name, StringComparison.Ordinal);
            var guidance = system.IndexOf(type.Guidance, StringComparison.Ordinal);
            var coaching = system.IndexOf(PromptBuilder.CoachingHeading, StringComparison.Ordinal);
            Assert.True(type.Guidance.Length > 0, $"{type.Name} has guidance");
            Assert.True(heading > 0, $"{type.Name} heading");
            Assert.True(guidance > heading, $"{type.Name} guidance after heading");
            Assert.True(coaching > guidance, $"{type.Name} before coaching notes");
        }
    }

    [Theory]
    [InlineData("recruiter SCREEN", "Recruiter screen")]
    [InlineData("  Panel ", "Panel")]
    [InlineData("does not exist", InterviewTypes.DefaultName)]
    [InlineData("", InterviewTypes.DefaultName)]
    [InlineData(null, InterviewTypes.DefaultName)]
    public void Interview_type_lookup_is_forgiving(string? stored, string expected)
    {
        Assert.Equal(expected, InterviewTypes.Find(stored).Name);
    }

    [Fact]
    public void Interview_type_options_are_well_formed()
    {
        Assert.Equal(InterviewTypes.DefaultName, InterviewTypes.All[0].Name);
        Assert.Equal(InterviewTypes.All.Count, InterviewTypes.All.Select(t => t.Name.ToLowerInvariant()).Distinct().Count());
        foreach (var type in InterviewTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(type.ShortName), type.Name);
            Assert.True(type.ShortName.Length <= 12, $"{type.Name} short name fits the chip");
            Assert.False(string.IsNullOrWhiteSpace(type.Description), type.Name);
        }
    }

    [Fact]
    public void Only_live_coding_allows_code()
    {
        foreach (var type in InterviewTypes.All)
        {
            var allowsCode = type.Guidance.Contains("fenced", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(type.Name == "Live coding", allowsCode);
        }
    }

    // ---- sections ----

    [Fact]
    public void Empty_profile_has_only_the_interview_type_and_coaching_sections()
    {
        var system = System(EmptyProfile());

        Assert.Equal(2, CountOccurrences(system, "## "));
        Assert.EndsWith(PromptBuilder.CoachingNotes.ReplaceLineEndings("\n").Trim(), system);
    }

    [Fact]
    public void Whitespace_only_fields_are_treated_as_empty()
    {
        var settings = EmptyProfile();
        settings.Resume = "  \r\n\t ";
        settings.JobDescription = "\n";
        settings.RoleTitle = " ";

        Assert.Equal(2, CountOccurrences(System(settings), "## "));
    }

    [Fact]
    public void All_sections_appear_in_order_when_present()
    {
        var settings = EmptyProfile();
        settings.InterviewType = "Technical";
        settings.RoleTitle = "Senior Backend Engineer";
        settings.CompanyName = "Acme";
        settings.Resume = "RESUME-TEXT";
        settings.JobDescription = "JD-TEXT";
        settings.ExtraNotes = "NOTES-TEXT";
        settings.CustomInstructions = "CUSTOM-TEXT";

        var system = System(settings);

        var type = system.IndexOf(PromptBuilder.InterviewTypeHeading + "Technical", StringComparison.Ordinal);
        var coaching = system.IndexOf(PromptBuilder.CoachingHeading, StringComparison.Ordinal);
        var role = system.IndexOf("## Role\nPosition: Senior Backend Engineer\nCompany: Acme", StringComparison.Ordinal);
        var resume = system.IndexOf("## Candidate resume\nRESUME-TEXT", StringComparison.Ordinal);
        var jd = system.IndexOf("## Job description\nJD-TEXT", StringComparison.Ordinal);
        var notes = system.IndexOf("## Candidate's notes\nNOTES-TEXT", StringComparison.Ordinal);
        var custom = system.IndexOf("## Extra instructions from the candidate (these override everything above)\nCUSTOM-TEXT", StringComparison.Ordinal);
        Assert.True(type > 0, "interview type section");
        Assert.True(coaching > type, "coaching notes after interview type");
        Assert.True(role > coaching, "role after coaching notes");
        Assert.True(resume > role, "resume after role");
        Assert.True(jd > resume, "job description after resume");
        Assert.True(notes > jd, "notes after job description");
        Assert.True(custom > notes, "custom instructions last");
        Assert.EndsWith("CUSTOM-TEXT", system);
    }

    [Theory]
    [InlineData("Data Scientist", "", "## Role\nPosition: Data Scientist")]
    [InlineData("", "Globex", "## Role\nCompany: Globex")]
    [InlineData(" Data Scientist ", " Globex ", "## Role\nPosition: Data Scientist\nCompany: Globex")]
    public void Role_section_uses_whichever_fields_are_set(string title, string company, string expected)
    {
        var settings = EmptyProfile();
        settings.RoleTitle = title;
        settings.CompanyName = company;

        var system = System(settings);

        Assert.EndsWith(expected, system);
    }

    [Theory]
    [InlineData(nameof(AppSettings.Resume), "## Candidate resume")]
    [InlineData(nameof(AppSettings.JobDescription), "## Job description")]
    [InlineData(nameof(AppSettings.ExtraNotes), "## Candidate's notes")]
    [InlineData(nameof(AppSettings.CustomInstructions), "## Extra instructions from the candidate (these override everything above)")]
    public void Each_section_appears_alone_when_only_its_field_is_set(string field, string heading)
    {
        var settings = EmptyProfile();
        const string content = "  Some content\r\nsecond line  ";
        switch (field)
        {
            case nameof(AppSettings.Resume): settings.Resume = content; break;
            case nameof(AppSettings.JobDescription): settings.JobDescription = content; break;
            case nameof(AppSettings.ExtraNotes): settings.ExtraNotes = content; break;
            default: settings.CustomInstructions = content; break;
        }

        var system = System(settings);

        Assert.EndsWith($"\n\n{heading}\nSome content\nsecond line", system);
        Assert.Equal(3, CountOccurrences(system, "\n## ")); // interview type + coaching notes are always there
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Long_sections_are_truncated_to_12000_characters()
    {
        var settings = EmptyProfile();
        settings.Resume = new string('r', 20_000) + "RESUME-TAIL";
        settings.JobDescription = new string('j', 12_000);

        var system = System(settings);

        var resume = SectionBody(system, "## Candidate resume");
        Assert.Equal(new string('r', 12_000) + "…", resume);
        Assert.DoesNotContain("RESUME-TAIL", system);
        // Exactly at the limit is kept whole.
        Assert.Equal(new string('j', 12_000), SectionBody(system, "## Job description"));
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var text = new string('a', PromptBuilder.MaxSectionChars - 1) + "😀" + "tail";

        var truncated = PromptBuilder.Truncate(text);

        Assert.Equal(new string('a', PromptBuilder.MaxSectionChars - 1) + "…", truncated);
    }

    private static string SectionBody(string system, string heading)
    {
        var start = system.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing {heading}");
        start += heading.Length + 1;
        var end = system.IndexOf("\n\n## ", start, StringComparison.Ordinal);
        return end < 0 ? system[start..] : system[start..end];
    }

    // ---- messages ----

    [Fact]
    public void Final_user_message_without_transcript_is_just_the_question()
    {
        var messages = _builder.Build(EmptyProfile(), "  What is your biggest weakness?  ", [], "   ");

        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[^1].Role);
        Assert.Equal("Interviewer's question: What is your biggest weakness?", messages[^1].Content);
    }

    [Fact]
    public void Final_user_message_includes_recent_transcript()
    {
        var messages = _builder.Build(EmptyProfile(), "And why did you leave?", [], "We talked about your last job at Initech.");

        Assert.Equal(
            "Recent conversation (live transcript, may be imperfect):\nWe talked about your last job at Initech.\n\nInterviewer's question: And why did you leave?",
            messages[^1].Content);
    }

    [Fact]
    public void History_is_limited_to_the_most_recent_completed_items_oldest_first()
    {
        var settings = EmptyProfile();
        settings.HistoryDepth = 2;
        QaItem[] history = [Qa("Q1", "A1"), Qa("Q2", "A2"), Qa("Q3", "A3")];

        var messages = _builder.Build(settings, "Q4", history, "");

        Assert.Equal(
            new[] { "system", "user", "assistant", "user", "assistant", "user" },
            messages.Select(m => m.Role));
        Assert.Equal(
            new[] { "Interviewer: Q2", "A2", "Interviewer: Q3", "A3", "Interviewer's question: Q4" },
            messages.Skip(1).Select(m => m.Content));
    }

    [Fact]
    public void History_skips_streaming_errored_and_empty_items()
    {
        var settings = EmptyProfile();
        settings.HistoryDepth = 3;
        QaItem[] history =
        [
            Qa("Q1", "A1"),
            Qa("Q2", "A2"),
            Qa("Q3", "partial", error: "OpenAI error 500: boom"),
            Qa("Q4", "   "),
            Qa("Q5", "A5"),
            Qa("", "orphan answer"),
            Qa("Q6", "still streaming", streaming: true),
        ];

        var messages = _builder.Build(settings, "Q7", history, "");

        Assert.Equal(
            new[] { "Interviewer: Q1", "A1", "Interviewer: Q2", "A2", "Interviewer: Q5", "A5", "Interviewer's question: Q7" },
            messages.Skip(1).Select(m => m.Content));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void History_depth_zero_or_negative_sends_no_history(int depth)
    {
        var settings = EmptyProfile();
        settings.HistoryDepth = depth;

        var messages = _builder.Build(settings, "Q2", [Qa("Q1", "A1")], "");

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void History_depth_larger_than_history_sends_everything()
    {
        var settings = EmptyProfile();
        settings.HistoryDepth = 10;

        var messages = _builder.Build(settings, "Q3", [Qa("Q1", "A1"), Qa("Q2", "A2")], "");

        Assert.Equal(6, messages.Count);
    }

    [Fact]
    public void History_answers_are_trimmed()
    {
        var messages = _builder.Build(EmptyProfile(), "Q2", [Qa(" Q1 ", "\n**A1**\n\n")], "");

        Assert.Equal("Interviewer: Q1", messages[1].Content);
        Assert.Equal("**A1**", messages[2].Content);
    }

    // ---- request ----

    [Fact]
    public void Non_reasoning_model_uses_temperature_and_answer_tokens()
    {
        var settings = EmptyProfile();
        settings.OpenAiModel = "gpt-4.1-mini";
        settings.OpenAiBaseUrl = "https://api.openai.com/v1";
        settings.OpenAiApiKey = " sk-abc ";
        settings.Temperature = 0.7;
        settings.MaxAnswerTokens = 800;
        settings.ReasoningEffort = "high";
        var messages = _builder.Build(settings, "Q", [], "");

        var request = _builder.BuildRequest(settings, messages);

        Assert.Equal("gpt-4.1-mini", request.Model);
        Assert.Equal("https://api.openai.com/v1", request.BaseUrl);
        Assert.Equal("sk-abc", request.ApiKey);
        Assert.Same(messages, request.Messages);
        Assert.Equal(0.7, request.Temperature);
        Assert.Null(request.ReasoningEffort);
        Assert.Equal(800, request.MaxTokens);
    }

    [Fact]
    public void Reasoning_model_drops_temperature_defaults_effort_and_adds_headroom()
    {
        var settings = EmptyProfile();
        settings.OpenAiModel = "gpt-5-mini";
        settings.Temperature = 0.7;
        settings.MaxAnswerTokens = 800;
        settings.ReasoningEffort = "";

        var request = _builder.BuildRequest(settings, []);

        Assert.Null(request.Temperature);
        Assert.Equal("low", request.ReasoningEffort);
        Assert.Equal(2800, request.MaxTokens);
    }

    [Fact]
    public void Reasoning_model_uses_the_configured_effort()
    {
        var settings = EmptyProfile();
        settings.OpenAiModel = "o3";
        settings.ReasoningEffort = "High";

        var request = _builder.BuildRequest(settings, []);

        Assert.Equal("high", request.ReasoningEffort);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(100, 100)]
    [InlineData(4000, 4000)]
    [InlineData(50_000, 4000)]
    [InlineData(-5, 100)]
    public void Max_answer_tokens_are_clamped(int configured, int expected)
    {
        var settings = EmptyProfile();
        settings.MaxAnswerTokens = configured;

        settings.OpenAiModel = "gpt-4o";
        Assert.Equal(expected, _builder.BuildRequest(settings, []).MaxTokens);

        settings.OpenAiModel = "o4-mini";
        Assert.Equal(expected + 2000, _builder.BuildRequest(settings, []).MaxTokens);
    }

    [Fact]
    public void Blank_model_and_base_url_fall_back_to_defaults()
    {
        var settings = EmptyProfile();
        settings.OpenAiModel = "  ";
        settings.OpenAiBaseUrl = "";

        var request = _builder.BuildRequest(settings, []);

        Assert.Equal("gpt-4.1-mini", request.Model);
        Assert.Equal("https://api.openai.com/v1", request.BaseUrl);
    }

    [Theory]
    [InlineData(double.NaN, 0.4)]
    [InlineData(5.0, 2.0)]
    [InlineData(-1.0, 0.0)]
    public void Temperature_is_kept_in_the_valid_range(double configured, double expected)
    {
        var settings = EmptyProfile();
        settings.OpenAiModel = "gpt-4o";
        settings.Temperature = configured;

        Assert.Equal(expected, _builder.BuildRequest(settings, []).Temperature);
    }

    [Theory]
    [InlineData("o1", true)]
    [InlineData("o1-mini", true)]
    [InlineData("o3", true)]
    [InlineData("o3-mini-2025-01-31", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-5", true)]
    [InlineData("GPT-5-mini", true)]
    [InlineData("gpt-5-nano", true)]
    [InlineData("gpt-5.1", true)]
    [InlineData(" gpt-5 ", true)]
    [InlineData("openai/o3-mini", true)]
    [InlineData("gpt-5-chat-latest", false)]
    [InlineData("gpt-5.1-chat-latest", false)]
    [InlineData("gpt-4.1-mini", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("chatgpt-4o-latest", false)]
    [InlineData("llama3.1:8b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Reasoning_models_are_detected(string model, bool expected)
    {
        Assert.Equal(expected, PromptBuilder.IsReasoningModel(model));
    }
}
