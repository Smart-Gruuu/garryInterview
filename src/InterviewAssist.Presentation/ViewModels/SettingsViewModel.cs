using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Settings;
using InterviewAssist.Presentation.Theming;

namespace InterviewAssist.Presentation.ViewModels;

public sealed record AudioSourceOption(AudioSourceKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An audio device choice; <see cref="Id"/> "" is the system default device.</summary>
public sealed record AudioDeviceOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record ReasoningEffortOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Settings tab. Controls bind to <see cref="Settings"/> directly or through the properties here, which map
/// pickers and sliders onto the string/number settings. Every change applies immediately and is auto-saved.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private const int MaxTokensStep = 100;

    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(30);
    private static readonly AppSettings s_defaults = new();

    // Everything the Settings tab covers is reset; the profile (Setup tab) and window placement are kept.
    // API keys are not in settings.json, so a reset never touches them.
    private static readonly string[] s_keptOnReset =
    [
        nameof(AppSettings.RoleTitle), nameof(AppSettings.CompanyName), nameof(AppSettings.JobDescription),
        nameof(AppSettings.Resume), nameof(AppSettings.ExtraNotes), nameof(AppSettings.InterviewType),
        nameof(AppSettings.WindowX), nameof(AppSettings.WindowY),
        nameof(AppSettings.WindowWidth), nameof(AppSettings.WindowHeight),
    ];

    private static readonly string[] s_nonChatModelMarkers =
    [
        "embedding", "whisper", "tts", "dall-e", "davinci", "babbage", "moderation",
        "audio", "realtime", "transcribe", "image", "search", "sora",
    ];

    // View-model properties that are computed from each settings property.
    private static readonly Dictionary<string, string[]> s_dependents = new()
    {
        [nameof(AppSettings.DeepgramApiKey)] = [nameof(DeepgramApiKey)],
        [nameof(AppSettings.OpenAiApiKey)] = [nameof(OpenAiApiKey)],
        [nameof(AppSettings.OpenAiBaseUrl)] = [nameof(OpenAiBaseUrl), nameof(BaseUrlError), nameof(HasBaseUrlError), nameof(IsCustomBaseUrl)],
        [nameof(AppSettings.AudioSource)] = [nameof(SelectedAudioSource), nameof(AudioSourceCaption)],
        [nameof(AppSettings.AudioDeviceId)] = [nameof(SelectedAudioDevice)],
        [nameof(AppSettings.TranscriptionLanguage)] = [nameof(SelectedLanguage)],
        [nameof(AppSettings.DeepgramModel)] = [nameof(SelectedDeepgramModel)],
        [nameof(AppSettings.EndpointingMs)] = [nameof(EndpointingValue), nameof(EndpointingText)],
        [nameof(AppSettings.OpenAiModel)] = [nameof(OpenAiModel), nameof(SelectedModelOption), nameof(IsReasoningModel), nameof(TemperatureCaption), nameof(ReasoningCaption)],
        [nameof(AppSettings.AnswerLength)] = [nameof(SelectedAnswerLength), nameof(AnswerStyleDescription)],
        [nameof(AppSettings.AnswerFormat)] = [nameof(SelectedAnswerFormat), nameof(AnswerStyleDescription)],
        [nameof(AppSettings.Temperature)] = [nameof(TemperatureValue), nameof(TemperatureText)],
        [nameof(AppSettings.MaxAnswerTokens)] = [nameof(MaxTokensText)],
        [nameof(AppSettings.HistoryDepth)] = [nameof(HistoryDepthValue), nameof(HistoryDepthText)],
        [nameof(AppSettings.ReasoningEffort)] = [nameof(SelectedReasoningEffort)],
        [nameof(AppSettings.Opacity)] = [nameof(OpacityPercent), nameof(OpacityText)],
        [nameof(AppSettings.Theme)] = [nameof(SelectedTheme)],
        [nameof(AppSettings.FontSize)] = [nameof(FontSizeValue), nameof(FontSizeText)],
        [nameof(AppSettings.AskHotkey)] = [nameof(SelectedAskHotkey), nameof(HotkeyConflict)],
        [nameof(AppSettings.ToggleWindowHotkey)] = [nameof(SelectedToggleHotkey), nameof(HotkeyConflict)],
    };

    private readonly IAudioCaptureService _audio;
    private readonly ITranscriptionClient _transcription;
    private readonly IChatCompletionClient _chat;
    private readonly IPlatformWindowService _window;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly IAppEnvironment _environment;
    private bool _updatingDevices;

    public SettingsViewModel(
        ISettingsService settingsService,
        IAudioCaptureService audio,
        ITranscriptionClient transcription,
        IChatCompletionClient chat,
        IPlatformWindowService windowService,
        IThemeService themeService,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        ILauncherService launcher,
        IAppEnvironment environment)
    {
        Settings = settingsService.Settings;
        _audio = audio;
        _transcription = transcription;
        _chat = chat;
        _window = windowService;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _launcher = launcher;
        _environment = environment;

        AudioSources = audio.SupportsSystemAudio
            ? [new(AudioSourceKind.SystemAudio, "System audio (interviewer)"), new(AudioSourceKind.Microphone, "Microphone")]
            : [new(AudioSourceKind.Microphone, "Microphone")];
        Themes = [.. themeService.ThemeNames];

        // A hand-edited hotkey that isn't a preset still shows up (and stays selectable).
        Hotkeys =
        [
            .. SettingOptions.Hotkeys
                .Concat([Settings.AskHotkey, Settings.ToggleWindowHotkey])
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        ReasoningEfforts =
        [
            .. SettingOptions.ReasoningEfforts.Select(v => new ReasoningEffortOption(
                v,
                v.Length == 0 ? $"Default ({PromptBuilder.DefaultReasoningEffort})" : char.ToUpperInvariant(v[0]) + v[1..])),
        ];

        Settings.PropertyChanged += OnSettingsPropertyChanged;
        windowService.Warning += OnWindowWarning;
    }

    public AppSettings Settings { get; }

    // ---- Platform capabilities (rows for unsupported features are hidden) ----

    public bool SupportsAlwaysOnTop => _window.SupportsAlwaysOnTop;

    public bool SupportsOpacity => _window.SupportsOpacity;

    public bool SupportsStealth => _window.SupportsStealth;

    public bool SupportsGlobalHotkeys => _window.SupportsGlobalHotkeys;

    // ---- API keys ----

    public string DeepgramApiKey
    {
        get => Settings.DeepgramApiKey;
        set => SetTrimmed(value, Settings.DeepgramApiKey, v => Settings.DeepgramApiKey = v);
    }

    public string OpenAiApiKey
    {
        get => Settings.OpenAiApiKey;
        set => SetTrimmed(value, Settings.OpenAiApiKey, v => Settings.OpenAiApiKey = v);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeepgramKeyHidden), nameof(DeepgramKeyToggleText))]
    private bool _showDeepgramKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAiKeyHidden), nameof(OpenAiKeyToggleText))]
    private bool _showOpenAiKey;

    public bool IsDeepgramKeyHidden => !ShowDeepgramKey;

    public bool IsOpenAiKeyHidden => !ShowOpenAiKey;

    public string DeepgramKeyToggleText => ShowDeepgramKey ? "Hide" : "Show";

    public string OpenAiKeyToggleText => ShowOpenAiKey ? "Hide" : "Show";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeepgramKeyStatus))]
    private string _deepgramKeyStatus = "";

    [ObservableProperty]
    private bool? _deepgramKeyOk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenAiKeyStatus))]
    private string _openAiKeyStatus = "";

    [ObservableProperty]
    private bool? _openAiKeyOk;

    public bool HasDeepgramKeyStatus => DeepgramKeyStatus.Length > 0;

    public bool HasOpenAiKeyStatus => OpenAiKeyStatus.Length > 0;

    public string OpenAiBaseUrl
    {
        get => Settings.OpenAiBaseUrl;
        set => SetTrimmed(value, Settings.OpenAiBaseUrl, v => Settings.OpenAiBaseUrl = v);
    }

    public string BaseUrlError => IsHttpUrl(Settings.OpenAiBaseUrl)
        ? ""
        : $"Enter a full URL, e.g. {s_defaults.OpenAiBaseUrl}";

    public bool HasBaseUrlError => BaseUrlError.Length > 0;

    public bool IsCustomBaseUrl => !string.Equals(Settings.OpenAiBaseUrl, s_defaults.OpenAiBaseUrl, StringComparison.OrdinalIgnoreCase);

    // ---- Transcription ----

    public AudioSourceOption[] AudioSources { get; }

    public AudioSourceOption? SelectedAudioSource
    {
        get => AudioSources.FirstOrDefault(o => o.Kind == Settings.AudioSource) ?? AudioSources[0];
        set
        {
            if (value is null || value.Kind == Settings.AudioSource)
                return;

            // A device of the old kind means nothing for the new one.
            Settings.AudioDeviceId = "";
            Settings.AudioSource = value.Kind;
        }
    }

    public string AudioSourceCaption => SelectedAudioSource?.Kind == AudioSourceKind.SystemAudio
        ? "Captures what this computer plays, i.e. the interviewer's voice in Zoom, Teams or Meet. Works with headphones."
        : "Captures a microphone. Use it when the interviewer is in the same room as this device.";

    [ObservableProperty]
    private List<AudioDeviceOption> _audioDevices = [new("", "Default device")];

    public AudioDeviceOption? SelectedAudioDevice
    {
        get => AudioDevices.FirstOrDefault(d => d.Id == Settings.AudioDeviceId) ?? AudioDevices[0];
        set
        {
            // The picker reports null while its item list is being replaced.
            if (_updatingDevices || value is null || value.Id == Settings.AudioDeviceId)
                return;
            Settings.AudioDeviceId = value.Id;
        }
    }

    public LanguageOption[] Languages { get; } = [.. SettingOptions.TranscriptionLanguages];

    public LanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => string.Equals(l.Code, Settings.TranscriptionLanguage, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && value.Code != Settings.TranscriptionLanguage)
                Settings.TranscriptionLanguage = value.Code;
        }
    }

    public string[] DeepgramModels { get; } = [.. SettingOptions.DeepgramModels];

    public string? SelectedDeepgramModel
    {
        get => Match(DeepgramModels, Settings.DeepgramModel);
        set
        {
            if (!string.IsNullOrEmpty(value) && value != Settings.DeepgramModel)
                Settings.DeepgramModel = value;
        }
    }

    public double EndpointingValue
    {
        get => Settings.EndpointingMs;
        set
        {
            var ms = (int)(Math.Round(Math.Clamp(value, 100, 1000) / 50) * 50);
            if (ms != Settings.EndpointingMs)
                Settings.EndpointingMs = ms;
        }
    }

    public string EndpointingText => $"{Settings.EndpointingMs} ms";

    // ---- AI answers ----

    public string OpenAiModel
    {
        get => Settings.OpenAiModel;
        set => SetTrimmed(value, Settings.OpenAiModel, v => Settings.OpenAiModel = v);
    }

    [ObservableProperty]
    private List<string> _modelOptions = [.. SettingOptions.SuggestedOpenAiModels];

    public string? SelectedModelOption
    {
        get => ModelOptions.FirstOrDefault(m => string.Equals(m, Settings.OpenAiModel, StringComparison.OrdinalIgnoreCase));
        set
        {
            // Null while the list is being replaced, or when the typed model isn't in the list.
            if (!string.IsNullOrEmpty(value) && !string.Equals(value, Settings.OpenAiModel, StringComparison.OrdinalIgnoreCase))
                Settings.OpenAiModel = value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsStatus))]
    private string _modelsStatus = "";

    [ObservableProperty]
    private bool? _modelsOk;

    public bool HasModelsStatus => ModelsStatus.Length > 0;

    public bool IsReasoningModel => PromptBuilder.IsReasoningModel(Settings.OpenAiModel);

    public string[] AnswerLengths { get; } = [.. SettingOptions.AnswerLengths];

    public string? SelectedAnswerLength
    {
        get => Match(AnswerLengths, Settings.AnswerLength);
        set
        {
            if (!string.IsNullOrEmpty(value) && value != Settings.AnswerLength)
                Settings.AnswerLength = value;
        }
    }

    public string[] AnswerFormats { get; } = [.. SettingOptions.AnswerFormats];

    public string? SelectedAnswerFormat
    {
        get => Match(AnswerFormats, Settings.AnswerFormat);
        set
        {
            if (!string.IsNullOrEmpty(value) && value != Settings.AnswerFormat)
                Settings.AnswerFormat = value;
        }
    }

    public string AnswerStyleDescription
    {
        get
        {
            var length = Settings.AnswerLength.ToLowerInvariant() switch
            {
                "short" => "20–30 seconds spoken.",
                "detailed" => "About two minutes spoken.",
                _ => "About a minute spoken.",
            };
            var format = Settings.AnswerFormat.ToLowerInvariant() switch
            {
                "hybrid" => "A bold answer to say first, then talking points.",
                "bullets" => "A one-line answer, then talking points.",
                _ => "Conversational, like talking face to face.",
            };
            return $"{length} {format}";
        }
    }

    public double TemperatureValue
    {
        get => Settings.Temperature;
        set
        {
            var temperature = Math.Round(Math.Clamp(value, 0, 1) * 20) / 20;
            if (Math.Abs(temperature - Settings.Temperature) > 1e-9)
                Settings.Temperature = temperature;
        }
    }

    public string TemperatureText => Settings.Temperature.ToString("0.00");

    public string TemperatureCaption => IsReasoningModel
        ? "Not used by this model: reasoning models ignore temperature."
        : "Lower = focused and consistent. Higher = more varied wording.";

    public bool CanDecreaseMaxTokens => Settings.MaxAnswerTokens > PromptBuilder.MinAnswerTokenLimit;

    public bool CanIncreaseMaxTokens => Settings.MaxAnswerTokens < PromptBuilder.MaxAnswerTokenLimit;

    public string MaxTokensText => $"Up to {Settings.MaxAnswerTokens:N0} tokens";

    public double HistoryDepthValue
    {
        get => Settings.HistoryDepth;
        set
        {
            var depth = (int)Math.Round(Math.Clamp(value, 0, 10));
            if (depth != Settings.HistoryDepth)
                Settings.HistoryDepth = depth;
        }
    }

    public string HistoryDepthText => Settings.HistoryDepth switch
    {
        <= 0 => "Off",
        1 => "Last answer",
        var n => $"Last {n} answers",
    };

    public ReasoningEffortOption[] ReasoningEfforts { get; }

    public ReasoningEffortOption? SelectedReasoningEffort
    {
        get => ReasoningEfforts.FirstOrDefault(o => string.Equals(o.Value, Settings.ReasoningEffort, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && value.Value != Settings.ReasoningEffort)
                Settings.ReasoningEffort = value.Value;
        }
    }

    public string ReasoningCaption => IsReasoningModel
        ? "How long the model thinks before answering. Low is fastest, which matters live."
        : "Only used by reasoning models (o-series, gpt-5).";

    // ---- Window & appearance ----

    public double OpacityPercent
    {
        get => Math.Round(Settings.Opacity * 100);
        set
        {
            var opacity = Math.Round(Math.Clamp(value, 30, 100)) / 100;
            if (Math.Abs(opacity - Settings.Opacity) > 1e-9)
                Settings.Opacity = opacity;
        }
    }

    public string OpacityText => $"{OpacityPercent:0} %";

    public string StealthCaption => _environment.Platform switch
    {
        AppPlatform.Windows => "Hidden from screen sharing & recordings, taskbar and Alt+Tab",
        AppPlatform.Android => "Hidden from screenshots, screen recordings and screen sharing",
        _ => "Not available on this system: it has no way to hide a window from screen capture",
    };

    public string[] Themes { get; }

    public string? SelectedTheme
    {
        get => Match(Themes, Settings.Theme) ?? Themes.FirstOrDefault();
        set
        {
            if (!string.IsNullOrEmpty(value) && value != Settings.Theme)
                Settings.Theme = value;
        }
    }

    public double FontSizeValue
    {
        get => Settings.FontSize;
        set
        {
            var size = Math.Round(Math.Clamp(value, FontSizeLimits.Min, FontSizeLimits.Max));
            if (Math.Abs(size - Settings.FontSize) > 1e-9)
                Settings.FontSize = size;
        }
    }

    public string FontSizeText => $"{Settings.FontSize:0}";

    public string[] Hotkeys { get; }

    public string? SelectedAskHotkey
    {
        get => Match(Hotkeys, Settings.AskHotkey);
        set => SetHotkey(value, isAsk: true);
    }

    public string? SelectedToggleHotkey
    {
        get => Match(Hotkeys, Settings.ToggleWindowHotkey);
        set => SetHotkey(value, isAsk: false);
    }

    public bool HotkeyConflict =>
        !string.Equals(Settings.AskHotkey, "None", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Settings.AskHotkey, Settings.ToggleWindowHotkey, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWindowWarning))]
    private string _windowWarning = "";

    public bool HasWindowWarning => WindowWarning.Length > 0;

    // ---- About ----

    public string VersionText => $"Interview Assist {_environment.VersionText}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResetStatus))]
    private string _resetStatus = "";

    public bool HasResetStatus => ResetStatus.Length > 0;

    /// <summary>Call when the page appears: audio devices may have been plugged in or out meanwhile.</summary>
    public void OnAppearing()
    {
        ResetStatus = "";
        RefreshDevices();
    }

    // ---- Commands ----

    [RelayCommand]
    private void ToggleDeepgramKeyVisibility() => ShowDeepgramKey = !ShowDeepgramKey;

    [RelayCommand]
    private void ToggleOpenAiKeyVisibility() => ShowOpenAiKey = !ShowOpenAiKey;

    [RelayCommand]
    private async Task TestDeepgramKeyAsync()
    {
        var key = Settings.DeepgramApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetDeepgramKeyStatus(false, "Enter a key first.");
            return;
        }

        DeepgramKeyOk = null;
        DeepgramKeyStatus = "Testing…";
        bool ok;
        string message;
        try
        {
            (ok, message) = await _transcription.TestApiKeyAsync(key);
        }
        catch (Exception ex)
        {
            (ok, message) = (false, $"Test failed: {ex.Message}");
        }

        // The key was edited while testing: this result no longer applies.
        if (key == Settings.DeepgramApiKey)
            SetDeepgramKeyStatus(ok, message);
    }

    [RelayCommand]
    private async Task TestOpenAiKeyAsync()
    {
        var key = Settings.OpenAiApiKey;
        var baseUrl = Settings.OpenAiBaseUrl;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetOpenAiKeyStatus(false, "Enter a key first.");
            return;
        }

        if (HasBaseUrlError)
        {
            SetOpenAiKeyStatus(false, BaseUrlError);
            return;
        }

        OpenAiKeyOk = null;
        OpenAiKeyStatus = "Testing…";
        var (models, error) = await FetchModelsAsync(baseUrl, key);
        if (key != Settings.OpenAiApiKey || baseUrl != Settings.OpenAiBaseUrl)
            return;

        if (models is null)
        {
            SetOpenAiKeyStatus(false, error);
            return;
        }

        ApplyModelList(models, baseUrl);
        var model = Settings.OpenAiModel;
        var message = IsOfficialOpenAi(baseUrl) && models.Count > 0
                      && !models.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? $"Key works, but model '{model}' isn't available to it. Pick another below."
            : "Key works.";
        SetOpenAiKeyStatus(true, message);
    }

    // Steps snap to multiples of 100, so a hand-edited 750 goes to 700 / 800.
    [RelayCommand(CanExecute = nameof(CanDecreaseMaxTokens))]
    private void DecreaseMaxTokens() => SetMaxTokens((Settings.MaxAnswerTokens - 1) / MaxTokensStep * MaxTokensStep);

    [RelayCommand(CanExecute = nameof(CanIncreaseMaxTokens))]
    private void IncreaseMaxTokens() => SetMaxTokens((Settings.MaxAnswerTokens / MaxTokensStep + 1) * MaxTokensStep);

    [RelayCommand]
    private void ResetBaseUrl() => Settings.OpenAiBaseUrl = s_defaults.OpenAiBaseUrl;

    [RelayCommand]
    private void RefreshDevices()
    {
        var kind = (SelectedAudioSource ?? AudioSources[0]).Kind;
        var devices = _audio.GetDevices().Where(d => d.Kind == kind).ToList();
        var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);

        var options = new List<AudioDeviceOption>(devices.Count + 1)
        {
            new("", defaultDevice is null ? "Default device" : $"Default device ({defaultDevice.Name})"),
        };
        options.AddRange(devices.Select(d => new AudioDeviceOption(d.Id, d.Name)));

        _updatingDevices = true;
        try
        {
            AudioDevices = options;
        }
        finally
        {
            _updatingDevices = false;
        }

        OnPropertyChanged(nameof(SelectedAudioDevice));
    }

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        var key = Settings.OpenAiApiKey;
        var baseUrl = Settings.OpenAiBaseUrl;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetModelsStatus(false, "Add your OpenAI key above first.");
            return;
        }

        if (HasBaseUrlError)
        {
            SetModelsStatus(false, BaseUrlError);
            return;
        }

        ModelsOk = null;
        ModelsStatus = "Loading models…";
        var (models, error) = await FetchModelsAsync(baseUrl, key);
        if (models is null)
        {
            SetModelsStatus(false, error);
            return;
        }

        var count = ApplyModelList(models, baseUrl);
        SetModelsStatus(true, count == 0
            ? "No chat models found; showing the suggested ones."
            : $"{count} models loaded. Pick one, or type any model id.");
    }

    [RelayCommand]
    private void DismissWindowWarning() => WindowWarning = "";

    [RelayCommand]
    private async Task OpenUrlAsync(string url)
    {
        string? problem;
        try
        {
            problem = await _launcher.OpenUrlAsync(new Uri(url)) ? null : "No browser is available.";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (problem is not null)
            await _dialogs.AlertAsync("Couldn't open the browser", $"Open {url} yourself. ({problem})");
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Reset settings?",
                "All settings go back to their defaults. Your API keys, resume, job description and notes are kept.",
                "Reset"))
        {
            return;
        }

        WindowWarning = "";
        SettingsService.ResetToDefaults(Settings, s_keptOnReset);
        ResetStatus = "Settings restored to their defaults.";
    }

    // ---- Helpers ----

    private async Task<(IReadOnlyList<string>? Models, string Error)> FetchModelsAsync(string baseUrl, string key)
    {
        try
        {
            using var cts = new CancellationTokenSource(ModelListTimeout);
            return (await _chat.ListModelsAsync(baseUrl, key, cts.Token), "");
        }
        catch (ChatClientException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (null, "The server didn't answer in time.");
        }
        catch (Exception ex)
        {
            return (null, $"Request failed: {ex.Message}");
        }
    }

    /// <summary>Shows the chat-capable models from <paramref name="models"/> in the picker; returns how many.</summary>
    private int ApplyModelList(IReadOnlyList<string> models, string baseUrl)
    {
        // Other OpenAI-compatible servers use arbitrary ids, so only OpenAI's own list is filtered.
        var usable = IsOfficialOpenAi(baseUrl) ? models.Where(IsChatModel).ToList() : models.ToList();
        ModelOptions = usable.Count > 0 ? usable : [.. SettingOptions.SuggestedOpenAiModels];
        OnPropertyChanged(nameof(SelectedModelOption));
        return usable.Count;
    }

    private static bool IsChatModel(string id)
    {
        var chatFamily = id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                         || id.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase)
                         || (id.Length > 1 && id[0] is 'o' or 'O' && char.IsAsciiDigit(id[1]));
        return chatFamily && !s_nonChatModelMarkers.Any(m => id.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOfficialOpenAi(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string? Match(IEnumerable<string> options, string value) =>
        options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));

    private void SetTrimmed(string? value, string current, Action<string> assign, [CallerMemberName] string propertyName = "")
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed != current)
            assign(trimmed); // the settings change is relayed back as this property's change
        else if (value != trimmed)
            OnPropertyChanged(propertyName); // only whitespace was added: show the trimmed text again
    }

    private void SetMaxTokens(int tokens) =>
        Settings.MaxAnswerTokens = Math.Clamp(tokens, PromptBuilder.MinAnswerTokenLimit, PromptBuilder.MaxAnswerTokenLimit);

    private void SetHotkey(string? value, bool isAsk)
    {
        if (string.IsNullOrEmpty(value) || value == (isAsk ? Settings.AskHotkey : Settings.ToggleWindowHotkey))
            return;

        // Cleared first: the window service reports a clash with the new hotkey right after it is set.
        WindowWarning = "";
        if (isAsk)
            Settings.AskHotkey = value;
        else
            Settings.ToggleWindowHotkey = value;
    }

    private void SetDeepgramKeyStatus(bool ok, string message)
    {
        DeepgramKeyOk = ok;
        DeepgramKeyStatus = $"{(ok ? "✓" : "✗")} {message}";
    }

    private void SetOpenAiKeyStatus(bool ok, string message)
    {
        OpenAiKeyOk = ok;
        OpenAiKeyStatus = $"{(ok ? "✓" : "✗")} {message}";
    }

    private void SetModelsStatus(bool ok, string message)
    {
        ModelsOk = ok;
        ModelsStatus = $"{(ok ? "✓" : "✗")} {message}";
    }

    private void OnWindowWarning(object? sender, string message)
    {
        if (_dispatcher.IsUiThread)
            WindowWarning = message;
        else
            _dispatcher.Post(() => WindowWarning = message);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_dispatcher.IsUiThread)
        {
            _dispatcher.Post(() => OnSettingsPropertyChanged(sender, e));
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(string.Empty);
            RefreshDevices();
            return;
        }

        if (s_dependents.TryGetValue(e.PropertyName, out var dependents))
        {
            foreach (var name in dependents)
                OnPropertyChanged(name);
        }

        switch (e.PropertyName)
        {
            case nameof(AppSettings.DeepgramApiKey):
                DeepgramKeyOk = null;
                DeepgramKeyStatus = "";
                break;
            case nameof(AppSettings.OpenAiApiKey) or nameof(AppSettings.OpenAiBaseUrl):
                OpenAiKeyOk = null;
                OpenAiKeyStatus = "";
                break;
            case nameof(AppSettings.AudioSource):
                RefreshDevices();
                break;
            case nameof(AppSettings.MaxAnswerTokens):
                DecreaseMaxTokensCommand.NotifyCanExecuteChanged();
                IncreaseMaxTokensCommand.NotifyCanExecuteChanged();
                break;
        }
    }
}
