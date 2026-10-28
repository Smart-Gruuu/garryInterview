using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Tests.Fakes;
using InterviewAssist.Presentation.Theming;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Presentation.Tests;

public class SettingsViewModelTests
{
    private readonly ManualUiDispatcher _dispatcher = new();
    private readonly FakeSettingsService _settingsService = new();
    private readonly FakeAudioCaptureService _audio = new();
    private readonly FakeTranscriptionClient _transcription = new();
    private readonly FakeChatClient _chat = new();
    private readonly FakeWindowService _window = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeLauncherService _launcher = new();
    private readonly FakeAppEnvironment _environment = new();

    private AppSettings Settings => _settingsService.Settings;

    private SettingsViewModel CreateViewModel() => new(
        _settingsService, _audio, _transcription, _chat, _window, new FakeThemeService(),
        _dispatcher, _dialogs, _launcher, _environment);

    // ---- audio ----

    [Fact]
    public void System_audio_is_offered_only_when_supported()
    {
        _audio.SupportsSystemAudio = false;
        Settings.AudioSource = AudioSourceKind.SystemAudio;

        var vm = CreateViewModel();

        var only = Assert.Single(vm.AudioSources);
        Assert.Equal(AudioSourceKind.Microphone, only.Kind);
        Assert.Same(only, vm.SelectedAudioSource);
    }

    [Fact]
    public void Devices_are_listed_for_the_selected_source_with_the_default_first()
    {
        _audio.Devices.AddRange(
        [
            new AudioDeviceInfo("spk", "Speakers", AudioSourceKind.SystemAudio, IsDefault: true),
            new AudioDeviceInfo("mic-1", "USB mic", AudioSourceKind.Microphone, IsDefault: true),
            new AudioDeviceInfo("mic-2", "Webcam mic", AudioSourceKind.Microphone, IsDefault: false),
        ]);
        Settings.AudioSource = AudioSourceKind.Microphone;
        Settings.AudioDeviceId = "mic-2";
        var vm = CreateViewModel();

        vm.OnAppearing();

        Assert.Equal(
            [new AudioDeviceOption("", "Default device (USB mic)"), new("mic-1", "USB mic"), new("mic-2", "Webcam mic")],
            vm.AudioDevices);
        Assert.Equal("mic-2", vm.SelectedAudioDevice?.Id);
    }

    [Fact]
    public void Changing_the_source_clears_the_device_and_relists()
    {
        _audio.Devices.Add(new AudioDeviceInfo("spk", "Speakers", AudioSourceKind.SystemAudio, IsDefault: false));
        Settings.AudioSource = AudioSourceKind.Microphone;
        Settings.AudioDeviceId = "mic-1";
        var vm = CreateViewModel();

        vm.SelectedAudioSource = vm.AudioSources.Single(o => o.Kind == AudioSourceKind.SystemAudio);

        Assert.Equal(AudioSourceKind.SystemAudio, Settings.AudioSource);
        Assert.Equal("", Settings.AudioDeviceId);
        Assert.Equal(["", "spk"], vm.AudioDevices.Select(d => d.Id));
        Assert.StartsWith("Captures what this computer plays", vm.AudioSourceCaption);
    }

    [Fact]
    public void Device_picker_reporting_null_does_not_reset_the_device()
    {
        Settings.AudioDeviceId = "spk";
        var vm = CreateViewModel();

        vm.SelectedAudioDevice = null;

        Assert.Equal("spk", Settings.AudioDeviceId);
    }

    // ---- sliders and pickers ----

    [Theory]
    [InlineData(333, 350)]
    [InlineData(10, 100)]
    [InlineData(5000, 1000)]
    public void Endpointing_snaps_to_50_ms_steps_within_range(double value, int expected)
    {
        var vm = CreateViewModel();

        vm.EndpointingValue = value;

        Assert.Equal(expected, Settings.EndpointingMs);
        Assert.Equal($"{expected} ms", vm.EndpointingText);
    }

    [Theory]
    [InlineData(0.43, 0.45)]
    [InlineData(-1, 0)]
    [InlineData(1.7, 1)]
    public void Temperature_snaps_to_005_steps_within_range(double value, double expected)
    {
        var vm = CreateViewModel();

        vm.TemperatureValue = value;

        Assert.Equal(expected, Settings.Temperature, 9);
    }

    [Theory]
    [InlineData(25, 0.3, "30 %")]
    [InlineData(80.4, 0.8, "80 %")]
    [InlineData(150, 1.0, "100 %")]
    public void Opacity_percent_maps_to_the_opacity_setting(double percent, double opacity, string text)
    {
        var vm = CreateViewModel();

        vm.OpacityPercent = percent;

        Assert.Equal(opacity, Settings.Opacity, 9);
        Assert.Equal(text, vm.OpacityText);
    }

    [Theory]
    [InlineData(30, FontSizeLimits.Max)]
    [InlineData(5, FontSizeLimits.Min)]
    [InlineData(15.6, 16)]
    public void Font_size_is_rounded_and_clamped(double value, double expected)
    {
        var vm = CreateViewModel();

        vm.FontSizeValue = value;

        Assert.Equal(expected, Settings.FontSize);
        Assert.Equal($"{expected:0}", vm.FontSizeText);
    }

    [Theory]
    [InlineData(0, "Off")]
    [InlineData(1, "Last answer")]
    [InlineData(3.4, "Last 3 answers")]
    [InlineData(25, "Last 10 answers")]
    public void History_depth_is_rounded_clamped_and_described(double value, string text)
    {
        var vm = CreateViewModel();

        vm.HistoryDepthValue = value;

        Assert.Equal(text, vm.HistoryDepthText);
    }

    [Fact]
    public void Max_tokens_step_by_100_and_stop_at_the_limits()
    {
        Settings.MaxAnswerTokens = 750;
        var vm = CreateViewModel();

        vm.DecreaseMaxTokensCommand.Execute(null);
        Assert.Equal(700, Settings.MaxAnswerTokens);

        vm.IncreaseMaxTokensCommand.Execute(null);
        Assert.Equal(800, Settings.MaxAnswerTokens);

        Settings.MaxAnswerTokens = PromptBuilder.MaxAnswerTokenLimit;
        Assert.False(vm.IncreaseMaxTokensCommand.CanExecute(null));
        Assert.True(vm.DecreaseMaxTokensCommand.CanExecute(null));

        Settings.MaxAnswerTokens = PromptBuilder.MinAnswerTokenLimit;
        Assert.False(vm.DecreaseMaxTokensCommand.CanExecute(null));
    }

    [Fact]
    public void Pickers_map_onto_the_string_settings()
    {
        var vm = CreateViewModel();

        vm.SelectedLanguage = vm.Languages.Single(l => l.Code == "de");
        vm.SelectedDeepgramModel = "nova-2";
        vm.SelectedAnswerLength = "Short";
        vm.SelectedAnswerFormat = "Bullets";
        vm.SelectedTheme = "Nord";
        vm.SelectedReasoningEffort = vm.ReasoningEfforts.Single(r => r.Value == "high");

        Assert.Equal("de", Settings.TranscriptionLanguage);
        Assert.Equal("nova-2", Settings.DeepgramModel);
        Assert.Equal("Short", Settings.AnswerLength);
        Assert.Equal("Bullets", Settings.AnswerFormat);
        Assert.Equal("Nord", Settings.Theme);
        Assert.Equal("high", Settings.ReasoningEffort);
        Assert.Equal("20–30 seconds spoken. A one-line answer, then talking points.", vm.AnswerStyleDescription);
    }

    [Fact]
    public void Picker_selections_match_settings_case_insensitively_and_null_is_ignored()
    {
        Settings.Theme = "nord";
        Settings.TranscriptionLanguage = "EN-us";
        var vm = CreateViewModel();

        Assert.Equal("Nord", vm.SelectedTheme);
        Assert.Equal("en-US", vm.SelectedLanguage?.Code);

        vm.SelectedTheme = null;
        vm.SelectedLanguage = null;
        Assert.Equal("nord", Settings.Theme);
        Assert.Equal("EN-us", Settings.TranscriptionLanguage);
    }

    [Fact]
    public void Reasoning_effort_options_label_the_default()
    {
        var vm = CreateViewModel();

        Assert.Equal(
            [$"Default ({PromptBuilder.DefaultReasoningEffort})", "Low", "Medium", "High"],
            vm.ReasoningEfforts.Select(r => r.Label));
    }

    [Fact]
    public void Model_captions_follow_whether_it_is_a_reasoning_model()
    {
        Settings.OpenAiModel = "gpt-4.1-mini";
        var vm = CreateViewModel();
        Assert.False(vm.IsReasoningModel);
        Assert.StartsWith("Lower = focused", vm.TemperatureCaption);

        vm.OpenAiModel = "  gpt-5-mini ";

        Assert.Equal("gpt-5-mini", Settings.OpenAiModel);
        Assert.True(vm.IsReasoningModel);
        Assert.StartsWith("Not used by this model", vm.TemperatureCaption);
        Assert.Equal("gpt-5-mini", vm.SelectedModelOption);
    }

    // ---- hotkeys ----

    [Fact]
    public void A_custom_hotkey_from_the_file_is_listed_and_conflicts_are_flagged()
    {
        Settings.AskHotkey = "Ctrl+Alt+F12";
        var vm = CreateViewModel();

        Assert.Contains("Ctrl+Alt+F12", vm.Hotkeys);
        Assert.Equal("Ctrl+Alt+F12", vm.SelectedAskHotkey);
        Assert.False(vm.HotkeyConflict);

        vm.SelectedToggleHotkey = "Ctrl+Alt+F12";
        Assert.True(vm.HotkeyConflict);

        vm.SelectedAskHotkey = "None";
        vm.SelectedToggleHotkey = "None";
        Assert.False(vm.HotkeyConflict);
    }

    [Fact]
    public void Window_warnings_are_shown_and_cleared_when_a_hotkey_changes()
    {
        var vm = CreateViewModel();

        _window.RaiseWarning("Ctrl+Shift+H is already used by another app.");
        Assert.True(vm.HasWindowWarning);

        vm.SelectedToggleHotkey = "F9";
        Assert.Equal("", vm.WindowWarning);

        _window.RaiseWarning("F9 is taken.");
        vm.DismissWindowWarningCommand.Execute(null);
        Assert.False(vm.HasWindowWarning);
    }

    // ---- platform ----

    [Theory]
    [InlineData(AppPlatform.Windows, "taskbar and Alt+Tab")]
    [InlineData(AppPlatform.Android, "screenshots")]
    [InlineData(AppPlatform.Linux, "Not available on this system")]
    public void Stealth_caption_describes_the_platform(AppPlatform platform, string expected)
    {
        _environment.Platform = platform;

        Assert.Contains(expected, CreateViewModel().StealthCaption);
    }

    [Fact]
    public void Capabilities_and_version_come_from_the_host()
    {
        _window.SupportsStealth = false;
        _window.SupportsGlobalHotkeys = false;
        _environment.VersionText = "2.1 (build 7)";

        var vm = CreateViewModel();

        Assert.False(vm.SupportsStealth);
        Assert.False(vm.SupportsGlobalHotkeys);
        Assert.True(vm.SupportsOpacity);
        Assert.Equal("Interview Assist 2.1 (build 7)", vm.VersionText);
    }

    // ---- change notification ----

    [Fact]
    public void Settings_changes_raise_the_dependent_properties()
    {
        var vm = CreateViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Settings.Opacity = 0.5;

        Assert.Contains(nameof(SettingsViewModel.OpacityPercent), changed);
        Assert.Contains(nameof(SettingsViewModel.OpacityText), changed);
    }

    [Fact]
    public void Changes_from_a_background_thread_are_marshalled_to_the_ui_thread()
    {
        var vm = CreateViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _dispatcher.IsUiThread = false;
        Settings.FontSize = 18;
        Assert.Empty(changed);

        _dispatcher.IsUiThread = true;
        _dispatcher.Drain();
        Assert.Contains(nameof(SettingsViewModel.FontSizeText), changed);
    }

    // ---- API keys ----

    [Fact]
    public void Keys_and_base_url_are_trimmed()
    {
        var vm = CreateViewModel();

        vm.DeepgramApiKey = "  dg-key\n";
        vm.OpenAiApiKey = " sk-key ";
        vm.OpenAiBaseUrl = " https://example.com/v1 ";

        Assert.Equal("dg-key", Settings.DeepgramApiKey);
        Assert.Equal("sk-key", Settings.OpenAiApiKey);
        Assert.Equal("https://example.com/v1", Settings.OpenAiBaseUrl);
        Assert.True(vm.IsCustomBaseUrl);

        vm.ResetBaseUrlCommand.Execute(null);
        Assert.False(vm.IsCustomBaseUrl);
    }

    [Fact]
    public void Base_url_must_be_an_http_url()
    {
        var vm = CreateViewModel();

        vm.OpenAiBaseUrl = "example.com";

        Assert.True(vm.HasBaseUrlError);
        Assert.StartsWith("Enter a full URL", vm.BaseUrlError);
    }

    [Fact]
    public void Key_visibility_toggles()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsDeepgramKeyHidden);
        Assert.Equal("Show", vm.DeepgramKeyToggleText);

        vm.ToggleDeepgramKeyVisibilityCommand.Execute(null);

        Assert.False(vm.IsDeepgramKeyHidden);
        Assert.Equal("Hide", vm.DeepgramKeyToggleText);
        Assert.True(vm.IsOpenAiKeyHidden);
    }

    [Fact]
    public async Task Deepgram_key_test_reports_the_result_and_resets_when_the_key_changes()
    {
        var vm = CreateViewModel();

        await vm.TestDeepgramKeyCommand.ExecuteAsync(null);
        Assert.Equal("✗ Enter a key first.", vm.DeepgramKeyStatus);
        Assert.Empty(_transcription.TestedKeys);

        Settings.DeepgramApiKey = "dg-key";
        _transcription.TestResult = (true, "Deepgram key works.");
        await vm.TestDeepgramKeyCommand.ExecuteAsync(null);
        Assert.Equal(["dg-key"], _transcription.TestedKeys);
        Assert.True(vm.DeepgramKeyOk);
        Assert.Equal("✓ Deepgram key works.", vm.DeepgramKeyStatus);

        Settings.DeepgramApiKey = "dg-other";
        Assert.Null(vm.DeepgramKeyOk);
        Assert.False(vm.HasDeepgramKeyStatus);

        _transcription.TestException = new HttpRequestException("offline");
        await vm.TestDeepgramKeyCommand.ExecuteAsync(null);
        Assert.False(vm.DeepgramKeyOk);
        Assert.Equal("✗ Test failed: offline", vm.DeepgramKeyStatus);
    }

    [Fact]
    public async Task OpenAi_key_test_loads_the_chat_models_and_warns_about_an_unavailable_model()
    {
        Settings.OpenAiApiKey = "sk-key";
        Settings.OpenAiModel = "gpt-4.1-mini";
        _chat.Models = ["gpt-4o", "text-embedding-3-small", "o3-mini", "whisper-1", "gpt-4o-realtime-preview", "dall-e-3"];
        var vm = CreateViewModel();

        await vm.TestOpenAiKeyCommand.ExecuteAsync(null);

        Assert.Equal([(Settings.OpenAiBaseUrl, "sk-key")], _chat.ModelListRequests);
        Assert.Equal(["gpt-4o", "o3-mini"], vm.ModelOptions);
        Assert.True(vm.OpenAiKeyOk);
        Assert.Equal("✓ Key works, but model 'gpt-4.1-mini' isn't available to it. Pick another below.", vm.OpenAiKeyStatus);
    }

    [Fact]
    public async Task OpenAi_key_test_failures_are_reported()
    {
        var vm = CreateViewModel();
        await vm.TestOpenAiKeyCommand.ExecuteAsync(null);
        Assert.Equal("✗ Enter a key first.", vm.OpenAiKeyStatus);

        Settings.OpenAiApiKey = "sk-bad";
        _chat.ListModelsException = new ChatClientException("Invalid API key.", 401);
        await vm.TestOpenAiKeyCommand.ExecuteAsync(null);
        Assert.False(vm.OpenAiKeyOk);
        Assert.Equal("✗ Invalid API key.", vm.OpenAiKeyStatus);
    }

    [Fact]
    public async Task Models_from_other_servers_are_listed_unfiltered()
    {
        Settings.OpenAiApiKey = "key";
        Settings.OpenAiBaseUrl = "http://localhost:11434/v1";
        _chat.Models = ["llama3.1:8b", "qwen2.5"];
        var vm = CreateViewModel();

        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.Equal(["llama3.1:8b", "qwen2.5"], vm.ModelOptions);
        Assert.Equal("✓ 2 models loaded. Pick one, or type any model id.", vm.ModelsStatus);
    }

    [Fact]
    public async Task No_chat_models_falls_back_to_the_suggested_list()
    {
        Settings.OpenAiApiKey = "sk-key";
        _chat.Models = ["whisper-1"];
        var vm = CreateViewModel();

        await vm.LoadModelsCommand.ExecuteAsync(null);

        Assert.Equal(SettingOptions.SuggestedOpenAiModels, vm.ModelOptions);
        Assert.Equal("✓ No chat models found; showing the suggested ones.", vm.ModelsStatus);
    }

    // ---- links and reset ----

    [Fact]
    public async Task Links_open_in_the_browser_and_failures_show_an_alert()
    {
        var vm = CreateViewModel();

        await vm.OpenUrlCommand.ExecuteAsync("https://console.deepgram.com/");
        Assert.Equal([new Uri("https://console.deepgram.com/")], _launcher.Opened);
        Assert.Empty(_dialogs.Alerts);

        _launcher.Result = false;
        await vm.OpenUrlCommand.ExecuteAsync("https://platform.openai.com/api-keys");
        var alert = Assert.Single(_dialogs.Alerts);
        Assert.Equal("Couldn't open the browser", alert.Title);
        Assert.Contains("https://platform.openai.com/api-keys", alert.Message);
    }

    [Fact]
    public async Task Reset_restores_defaults_but_keeps_the_profile_interview_type_window_and_keys()
    {
        Settings.Theme = "Nord";
        Settings.FontSize = 20;
        Settings.AnswerFormat = "Bullets";
        Settings.Resume = "My resume";
        Settings.JobDescription = "The job";
        Settings.RoleTitle = "Engineer";
        Settings.InterviewType = "Technical";
        Settings.WindowWidth = 600;
        Settings.DeepgramApiKey = "dg-key";
        Settings.OpenAiApiKey = "sk-key";
        var vm = CreateViewModel();

        await vm.ResetSettingsCommand.ExecuteAsync(null);

        var defaults = new AppSettings();
        Assert.Single(_dialogs.Confirms);
        Assert.Equal(defaults.Theme, Settings.Theme);
        Assert.Equal(defaults.FontSize, Settings.FontSize);
        Assert.Equal(defaults.AnswerFormat, Settings.AnswerFormat);
        Assert.Equal("My resume", Settings.Resume);
        Assert.Equal("The job", Settings.JobDescription);
        Assert.Equal("Engineer", Settings.RoleTitle);
        Assert.Equal("Technical", Settings.InterviewType);
        Assert.Equal(600, Settings.WindowWidth);
        Assert.Equal("dg-key", Settings.DeepgramApiKey);
        Assert.Equal("sk-key", Settings.OpenAiApiKey);
        Assert.Equal("Settings restored to their defaults.", vm.ResetStatus);

        vm.OnAppearing();
        Assert.False(vm.HasResetStatus);
    }

    [Fact]
    public async Task Reset_does_nothing_when_not_confirmed()
    {
        Settings.Theme = "Nord";
        _dialogs.ConfirmResult = false;
        var vm = CreateViewModel();

        await vm.ResetSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Nord", Settings.Theme);
        Assert.False(vm.HasResetStatus);
    }
}
