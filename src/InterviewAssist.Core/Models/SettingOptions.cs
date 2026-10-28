namespace InterviewAssist.Core.Models;

/// <summary>Allowed values for the string-typed settings, shared by the settings UI and the services.</summary>
public static class SettingOptions
{
    public static readonly IReadOnlyList<string> AnswerLengths = ["Short", "Medium", "Detailed"];

    /// <summary>Script = conversational spoken sentences; Hybrid = bold 1-2 sentence answer + talking points; Bullets = talking points only.</summary>
    public static readonly IReadOnlyList<string> AnswerFormats = ["Script", "Hybrid", "Bullets"];

    public static readonly IReadOnlyList<string> Themes = ["Dark", "Light", "Midnight", "Nord", "Terminal"];

    /// <summary>Global hotkey presets. "None" disables the hotkey.</summary>
    public static readonly IReadOnlyList<string> Hotkeys =
    [
        "None", "Ctrl+Shift+Enter", "Ctrl+Enter", "Alt+Enter", "Ctrl+Shift+Space",
        "Ctrl+Shift+A", "Ctrl+Shift+H", "Ctrl+Alt+H", "F8", "F9", "F10",
    ];

    public static readonly IReadOnlyList<string> DeepgramModels = ["nova-3", "nova-2"];

    public static readonly IReadOnlyList<string> SuggestedOpenAiModels =
        ["gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini", "gpt-4o", "gpt-5-mini", "gpt-5"];

    public static readonly IReadOnlyList<string> ReasoningEfforts = ["", "low", "medium", "high"];

    /// <summary>Deepgram language codes with display names. "multi" = automatic multilingual (nova-2/nova-3).</summary>
    public static readonly IReadOnlyList<LanguageOption> TranscriptionLanguages =
    [
        new("en", "English"),
        new("en-US", "English (US)"),
        new("en-GB", "English (UK)"),
        new("en-IN", "English (India)"),
        new("multi", "Multilingual (auto-detect)"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("de", "German"),
        new("pt", "Portuguese"),
        new("it", "Italian"),
        new("nl", "Dutch"),
        new("hi", "Hindi"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh", "Chinese"),
        new("ru", "Russian"),
    ];
}

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}
