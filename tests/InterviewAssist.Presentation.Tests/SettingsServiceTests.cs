using System.Text.Json;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Settings;
using InterviewAssist.Presentation.Tests.Fakes;

namespace InterviewAssist.Presentation.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private const string DeepgramKeyName = "deepgram_api_key";
    private const string OpenAiKeyName = "openai_api_key";

    private readonly TempSettingsLocation _location = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly ManualUiDispatcher _dispatcher = new();
    private readonly List<SettingsService> _services = [];

    public void Dispose()
    {
        // Flushing also stops each service's debounce timer, so nothing writes into the folder after it is deleted.
        foreach (var service in _services)
            service.Flush();
        _location.Dispose();
    }

    private SettingsService CreateService()
    {
        var service = new SettingsService(_location, _secrets, _dispatcher);
        _services.Add(service);
        return service;
    }

    private void WriteSettingsFile(string json) => File.WriteAllText(_location.SettingsPath, json);

    private string ReadSettingsFile() => File.ReadAllText(_location.SettingsPath);

    [Fact]
    public void Missing_file_gives_the_defaults()
    {
        var settings = CreateService().Settings;

        var defaults = new AppSettings();
        Assert.Equal(defaults.Theme, settings.Theme);
        Assert.Equal(defaults.FontSize, settings.FontSize);
        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.False(File.Exists(_location.SettingsPath));
    }

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        var first = CreateService();
        var s = first.Settings;
        s.Theme = "Nord";
        s.FontSize = 18;
        s.AudioSource = AudioSourceKind.Microphone;
        s.FinalizeOnAsk = false;
        s.HistoryDepth = 7;
        s.Resume = "Zoë Müller\nSenior engineer — 10 years";
        s.AskHotkey = "Ctrl+Alt+H";
        s.WindowX = 120.5;
        s.WindowY = double.NaN;
        first.Flush();

        var json = ReadSettingsFile();
        Assert.Contains("\"Ctrl+Alt+H\"", json); // not escaped as +
        Assert.Contains("Zoë Müller", json);
        Assert.Contains("\"windowY\": \"NaN\"", json);
        Assert.Contains("\"audioSource\": \"Microphone\"", json);

        var loaded = CreateService().Settings;
        Assert.Equal("Nord", loaded.Theme);
        Assert.Equal(18, loaded.FontSize);
        Assert.Equal(AudioSourceKind.Microphone, loaded.AudioSource);
        Assert.False(loaded.FinalizeOnAsk);
        Assert.Equal(7, loaded.HistoryDepth);
        Assert.Equal(s.Resume, loaded.Resume);
        Assert.Equal("Ctrl+Alt+H", loaded.AskHotkey);
        Assert.Equal(120.5, loaded.WindowX);
        Assert.True(double.IsNaN(loaded.WindowY));
    }

    [Fact]
    public async Task SaveAsync_writes_immediately()
    {
        var service = CreateService();
        service.Settings.Theme = "Light";

        await service.SaveAsync();

        Assert.Equal("Light", CreateService().Settings.Theme);
    }

    [Fact]
    public void Flush_without_changes_writes_nothing()
    {
        CreateService().Flush();

        Assert.False(File.Exists(_location.SettingsPath));
    }

    [Fact]
    public void File_from_before_v2_moves_the_old_hybrid_default_to_script_and_saves_the_version()
    {
        WriteSettingsFile("""{ "answerFormat": "Hybrid", "theme": "Midnight" }""");

        var service = CreateService();

        Assert.Equal("Script", service.Settings.AnswerFormat);
        Assert.Equal("Midnight", service.Settings.Theme);
        Assert.Equal(AppSettings.CurrentVersion, service.Settings.SettingsVersion);

        service.Flush();
        using var saved = JsonDocument.Parse(ReadSettingsFile());
        Assert.Equal(AppSettings.CurrentVersion, saved.RootElement.GetProperty("settingsVersion").GetInt32());
        Assert.Equal("Script", saved.RootElement.GetProperty("answerFormat").GetString());
    }

    [Fact]
    public void Hybrid_chosen_in_a_v2_file_is_kept()
    {
        WriteSettingsFile("""{ "settingsVersion": 2, "answerFormat": "Hybrid" }""");

        Assert.Equal("Hybrid", CreateService().Settings.AnswerFormat);
    }

    [Fact]
    public void Unknown_and_invalid_values_are_ignored()
    {
        WriteSettingsFile(
            """
            {
              // comments and trailing commas are tolerated
              "settingsVersion": 2,
              "theme": "Nord",
              "fontSize": "huge",
              "audioSource": "Bluetooth",
              "historyDepth": 2.5,
              "someFutureSetting": true,
            }
            """);

        var settings = CreateService().Settings;
        var defaults = new AppSettings();

        Assert.Equal("Nord", settings.Theme);
        Assert.Equal(defaults.FontSize, settings.FontSize);
        Assert.Equal(defaults.AudioSource, settings.AudioSource);
        Assert.Equal(defaults.HistoryDepth, settings.HistoryDepth);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    public void Unreadable_file_falls_back_to_the_defaults(string content)
    {
        WriteSettingsFile(content);

        var settings = CreateService().Settings;

        Assert.Equal(new AppSettings().Theme, settings.Theme);
        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
    }

    [Fact]
    public async Task Api_keys_go_to_the_secret_store_trimmed_and_never_into_the_json()
    {
        var service = CreateService();
        service.Settings.DeepgramApiKey = "  dg-secret  ";
        service.Settings.OpenAiApiKey = "sk-secret";
        service.Settings.Theme = "Terminal";

        await _secrets.WaitUntilAsync(v =>
            v.TryGetValue(DeepgramKeyName, out var dg) && dg == "dg-secret"
            && v.TryGetValue(OpenAiKeyName, out var oa) && oa == "sk-secret");
        service.Flush();

        var json = ReadSettingsFile();
        Assert.Contains("Terminal", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clearing_a_key_removes_it_from_the_store()
    {
        _secrets.Seed(OpenAiKeyName, "sk-old");
        var service = CreateService();
        await service.LoadAsync();

        service.Settings.OpenAiApiKey = "";

        await _secrets.WaitUntilAsync(v => !v.ContainsKey(OpenAiKeyName));
    }

    [Fact]
    public async Task LoadAsync_reads_the_keys_without_writing_anything_back()
    {
        _secrets.Seed(DeepgramKeyName, " dg-stored ");
        _secrets.Seed(OpenAiKeyName, "sk-stored");
        var service = CreateService();

        await service.LoadAsync();

        Assert.Equal("dg-stored", service.Settings.DeepgramApiKey);
        Assert.Equal("sk-stored", service.Settings.OpenAiApiKey);
        Assert.True(service.Settings.HasDeepgramKey);
        Assert.Equal(0, _secrets.SetCount);

        service.Flush();
        Assert.False(File.Exists(_location.SettingsPath));
    }

    [Fact]
    public async Task A_key_typed_while_the_keys_are_loading_wins_over_the_stored_one()
    {
        _secrets.Seed(DeepgramKeyName, "dg-stored");
        _secrets.Seed(OpenAiKeyName, "sk-stored");
        var storeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _secrets.GetGate = storeReady.Task;
        var service = CreateService();

        var loading = service.LoadAsync();
        service.Settings.DeepgramApiKey = "dg-typed";
        storeReady.SetResult();
        await loading;

        Assert.Equal("dg-typed", service.Settings.DeepgramApiKey);
        Assert.Equal("sk-stored", service.Settings.OpenAiApiKey);

        // The typed key is written after the load, so it replaces the stored one.
        await _secrets.WaitUntilAsync(v => v.TryGetValue(DeepgramKeyName, out var dg) && dg == "dg-typed");
    }

    [Fact]
    public void ResetToDefaults_resets_everything_except_the_kept_properties_and_the_keys()
    {
        var settings = new AppSettings
        {
            Theme = "Nord",
            FontSize = 20,
            AnswerLength = "Detailed",
            Resume = "My resume",
            InterviewType = "Technical",
            DeepgramApiKey = "dg-key",
        };

        SettingsService.ResetToDefaults(settings, [nameof(AppSettings.Resume), nameof(AppSettings.InterviewType)]);

        var defaults = new AppSettings();
        Assert.Equal(defaults.Theme, settings.Theme);
        Assert.Equal(defaults.FontSize, settings.FontSize);
        Assert.Equal(defaults.AnswerLength, settings.AnswerLength);
        Assert.Equal("My resume", settings.Resume);
        Assert.Equal("Technical", settings.InterviewType);
        Assert.Equal("dg-key", settings.DeepgramApiKey);
    }
}
