using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Documents;
using InterviewAssist.Core.Transcription;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Settings;
using InterviewAssist.Presentation.ViewModels;
using InterviewAssist.Services;
using InterviewAssist.Views;
using Microsoft.Extensions.Logging;

namespace InterviewAssist;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var services = builder.Services;

        // Presentation services backed by MAUI.
        services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        services.AddSingleton<INavigationService, MauiNavigationService>();
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton<IDialogService, MauiDialogService>();
        services.AddSingleton<ILauncherService, MauiLauncherService>();
        services.AddSingleton<IAppEnvironment, MauiAppEnvironment>();
        services.AddSingleton<ISettingsLocation, MauiSettingsLocation>();
        services.AddSingleton<ISecretStore, MauiSecretStore>();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ITranscriptStore, TranscriptStore>();
        services.AddSingleton<ITranscriptionClient, DeepgramTranscriptionClient>();
        services.AddSingleton<IChatCompletionClient>(_ => new OpenAiChatClient());
        services.AddSingleton<IPromptBuilder, PromptBuilder>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

#if WINDOWS
        services.AddSingleton<IAudioCaptureService, Native.WindowsAudioCaptureService>();
        // App attaches the window through the concrete type; view models use the interface. Same instance.
        services.AddSingleton<Native.WindowsWindowService>();
        services.AddSingleton<IPlatformWindowService>(sp => sp.GetRequiredService<Native.WindowsWindowService>());
#elif ANDROID
        services.AddSingleton<IAudioCaptureService, Native.AndroidAudioCaptureService>();
        services.AddSingleton<Native.AndroidWindowService>();
        services.AddSingleton<IPlatformWindowService>(sp => sp.GetRequiredService<Native.AndroidWindowService>());
#endif

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Shell creates the tab pages from their DataTemplates through this container.
        services.AddSingleton<MainPage>();
        services.AddSingleton<SetupPage>();
        services.AddSingleton<SettingsPage>();

        return builder.Build();
    }
}
