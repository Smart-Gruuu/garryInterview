using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Theming;

namespace InterviewAssist.Presentation.Tests.Fakes;

/// <summary>Holds a plain <see cref="AppSettings"/>; nothing is persisted.</summary>
public sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Settings { get; } = new();

    public Task LoadAsync() => Task.CompletedTask;

    public Task SaveAsync() => Task.CompletedTask;
}

public sealed class FakeWindowService : IPlatformWindowService
{
    public bool SupportsAlwaysOnTop { get; set; } = true;

    public bool SupportsOpacity { get; set; } = true;

    public bool SupportsStealth { get; set; } = true;

    public bool SupportsGlobalHotkeys { get; set; } = true;

    public bool InterceptEnterKey { get; set; }

    public int ToggleCount { get; private set; }

    public event EventHandler<WindowCommand>? CommandReceived;

    public event EventHandler<string>? Warning;

    public void ToggleVisibility() => ToggleCount++;

    public void RaiseCommand(WindowCommand command) => CommandReceived?.Invoke(this, command);

    public void RaiseWarning(string message) => Warning?.Invoke(this, message);
}

public sealed class FakeNavigationService : INavigationService
{
    public List<AppPage> Visited { get; } = [];

    public Task GoToAsync(AppPage page)
    {
        Visited.Add(page);
        return Task.CompletedTask;
    }
}

public sealed class FakeClipboardService : IClipboardService
{
    public string? Text { get; private set; }

    public Exception? Exception { get; set; }

    public Task SetTextAsync(string text)
    {
        if (Exception is not null)
        {
            return Task.FromException(Exception);
        }

        Text = text;
        return Task.CompletedTask;
    }
}

public sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public string? ChooseResult { get; set; }

    public List<(string Title, string Message, string Accept)> Confirms { get; } = [];

    public List<(string Title, string Message)> Alerts { get; } = [];

    public List<(string Title, IReadOnlyList<string> Options)> Chooses { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel")
    {
        Confirms.Add((title, message, accept));
        return Task.FromResult(ConfirmResult);
    }

    public Task AlertAsync(string title, string message, string ok = "OK")
    {
        Alerts.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel")
    {
        Chooses.Add((title, options));
        return Task.FromResult(ChooseResult);
    }
}

public sealed class FakeFilePickerService : IFilePickerService
{
    public PickedFile? Result { get; set; }

    public Exception? Exception { get; set; }

    public List<(string Title, IReadOnlyList<string> Extensions)> Requests { get; } = [];

    public Task<PickedFile?> PickDocumentAsync(string title, IReadOnlyList<string> extensions)
    {
        Requests.Add((title, extensions));
        return Exception is null ? Task.FromResult(Result) : Task.FromException<PickedFile?>(Exception);
    }

    /// <summary>A picked file whose content is <paramref name="content"/>.</summary>
    public static PickedFile File(string fileName, byte[] content) =>
        new(fileName, () => Task.FromResult<Stream>(new MemoryStream(content)));
}

public sealed class FakeLauncherService : ILauncherService
{
    public bool Result { get; set; } = true;

    public Exception? Exception { get; set; }

    public List<Uri> Opened { get; } = [];

    public Task<bool> OpenUrlAsync(Uri uri)
    {
        Opened.Add(uri);
        return Exception is null ? Task.FromResult(Result) : Task.FromException<bool>(Exception);
    }
}

public sealed class FakeAppEnvironment : IAppEnvironment
{
    public AppPlatform Platform { get; set; } = AppPlatform.Linux;

    public string VersionText { get; set; } = "1.0 (build 1)";
}

public sealed class FakeThemeService : IThemeService
{
    public IReadOnlyList<string> ThemeNames { get; } = ThemePalettes.Names;

    public int InitializeCount { get; private set; }

    public void Initialize() => InitializeCount++;
}
