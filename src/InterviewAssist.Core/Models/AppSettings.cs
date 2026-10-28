using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InterviewAssist.Core.Models;

/// <summary>
/// Every user-configurable value. One instance lives for the whole app (owned by ISettingsService);
/// UI binds to it directly and services react to PropertyChanged, so changes apply live and are auto-saved.
/// API keys are excluded from the JSON file and kept in platform secure storage instead.
/// </summary>
public partial class AppSettings : ObservableObject
{
    /// <summary>
    /// Bumped when a stored value must be migrated. 2: the default answer format became "Script"
    /// (conversational answers), so files saved with the old "Hybrid" default are moved over once.
    /// </summary>
    public const int CurrentVersion = 2;

    [ObservableProperty] private int _settingsVersion = CurrentVersion;

    // ---- API keys (secure storage, never written to settings.json) ----
    [ObservableProperty][property: JsonIgnore] private string _deepgramApiKey = "";
    [ObservableProperty][property: JsonIgnore] private string _openAiApiKey = "";

    // ---- OpenAI / answer generation ----
    [ObservableProperty] private string _openAiBaseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _openAiModel = "gpt-4.1-mini";
    [ObservableProperty] private double _temperature = 0.4;           // ignored for reasoning models
    [ObservableProperty] private int _maxAnswerTokens = 700;
    [ObservableProperty] private string _reasoningEffort = "";         // "", "low", "medium", "high" â€” only sent to reasoning models
    [ObservableProperty] private string _interviewType = InterviewTypes.DefaultName; // InterviewTypes.All names
    [ObservableProperty] private string _answerLength = "Medium";      // SettingOptions.AnswerLengths
    [ObservableProperty] private string _answerFormat = "Script";      // SettingOptions.AnswerFormats
    [ObservableProperty] private string _answerLanguage = "";          // "" = same language as the question
    [ObservableProperty] private string _customInstructions = "";
    [ObservableProperty] private int _historyDepth = 4;                // previous Q&A pairs sent as context

    // ---- Transcription (Deepgram) ----
    [ObservableProperty] private string _deepgramModel = "nova-3";
    [ObservableProperty] private string _transcriptionLanguage = "en"; // SettingOptions.TranscriptionLanguages codes
    [ObservableProperty] private AudioSourceKind _audioSource = AudioSourceKind.SystemAudio;
    [ObservableProperty] private string _audioDeviceId = "";           // "" = system default device of AudioSource kind
    [ObservableProperty] private int _endpointingMs = 300;
    [ObservableProperty] private bool _finalizeOnAsk = true;           // flush Deepgram before taking the question
    [ObservableProperty] private string _customVocabulary = "";        // comma-separated key terms (nova-3 keyterm prompting)

    // ---- Interview profile ----
    [ObservableProperty] private string _roleTitle = "";
    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private string _jobDescription = "";
    [ObservableProperty] private string _resume = "";
    [ObservableProperty] private string _extraNotes = "";

    // ---- Window & appearance ----
    [ObservableProperty] private bool _alwaysOnTop = true;
    [ObservableProperty] private double _opacity = 0.95;               // 0.3 .. 1.0
    [ObservableProperty] private bool _stealthMode = false;            // hide from screen capture, taskbar and Alt+Tab
    [ObservableProperty] private string _theme = "Dark";               // SettingOptions.Themes
    [ObservableProperty] private double _fontSize = 14;                // 11 .. 22
    [ObservableProperty] private bool _showTranscript = true;
    [ObservableProperty] private string _askHotkey = "Ctrl+Shift+Enter";    // global hotkey, SettingOptions.Hotkeys
    [ObservableProperty] private string _toggleWindowHotkey = "Ctrl+Shift+H"; // global hotkey: hide/show window

    // ---- Window bounds (restored on launch; NaN = let the OS decide) ----
    [ObservableProperty] private double _windowX = double.NaN;
    [ObservableProperty] private double _windowY = double.NaN;
    [ObservableProperty] private double _windowWidth = 460;
    [ObservableProperty] private double _windowHeight = 720;

    [JsonIgnore]
    public bool HasDeepgramKey => !string.IsNullOrWhiteSpace(DeepgramApiKey);

    [JsonIgnore]
    public bool HasOpenAiKey => !string.IsNullOrWhiteSpace(OpenAiApiKey);

    [JsonIgnore]
    public bool HasProfile => !string.IsNullOrWhiteSpace(Resume) || !string.IsNullOrWhiteSpace(JobDescription);

    partial void OnDeepgramApiKeyChanged(string value) => OnPropertyChanged(nameof(HasDeepgramKey));
    partial void OnOpenAiApiKeyChanged(string value) => OnPropertyChanged(nameof(HasOpenAiKey));
    partial void OnResumeChanged(string value) => OnPropertyChanged(nameof(HasProfile));
    partial void OnJobDescriptionChanged(string value) => OnPropertyChanged(nameof(HasProfile));
}
