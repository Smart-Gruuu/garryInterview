using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Documents;
using InterviewAssist.Core.Transcription;
using InterviewAssist.Desktop.Platform;
using InterviewAssist.Desktop.Services;
using InterviewAssist.Desktop.ViewModels;
using InterviewAssist.Desktop.Views;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Settings;
using InterviewAssist.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Desktop;

/// <summary>The desktop app's service registrations. Everything is a singleton.</summary>
public static class Composition
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(DesktopLogger<>));

        // Presentation services backed by Avalonia and the desktop OS.
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<INavigationService, AvaloniaNavigationService>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.AddSingleton<ILauncherService, AvaloniaLauncherService>();
        services.AddSingleton<IAppEnvironment, DesktopAppEnvironment>();
        services.AddSingleton<ISettingsLocation, DesktopSettingsLocation>();
        services.AddSingleton<ISecretStore, FileSecretStore>();

        // The main window attaches these through their concrete types; view models use the interfaces. Same instances.
        services.AddSingleton<AvaloniaDialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<DesktopWindowService>();
        services.AddSingleton<IPlatformWindowService>(sp => sp.GetRequiredService<DesktopWindowService>());

        services.AddSingleton<IThemeService, AvaloniaThemeService>();
        services.AddSingleton<ITranscriptStore, TranscriptStore>();
        services.AddSingleton<ITranscriptionClient, DeepgramTranscriptionClient>();
        services.AddSingleton<IChatCompletionClient>(_ => new OpenAiChatClient());
        services.AddSingleton<IPromptBuilder, PromptBuilder>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        AddAudioCapture(services);

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<LiveView>();
        services.AddSingleton<SetupView>();
        services.AddSingleton<SettingsView>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static void AddAudioCapture(ServiceCollection services)
    {
        if (OperatingSystem.IsLinux())
            services.AddSingleton<IAudioCaptureService, PulseAudioCaptureService>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<IAudioCaptureService, Native.WindowsAudioCaptureService>();
        else
            services.AddSingleton<IAudioCaptureService, UnsupportedAudioCaptureService>();
    }
}
