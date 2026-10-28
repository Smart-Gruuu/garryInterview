using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Desktop.Platform;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = Composition.BuildServiceProvider();
            _logger = _services.GetRequiredService<ILogger<App>>();

            // Theme before the first window so it never flashes the defaults; keys load in the background.
            _services.GetRequiredService<IThemeService>().Initialize();
            _ = LoadApiKeysAsync(_services.GetRequiredService<ISettingsService>());

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = _services.GetRequiredService<MainWindow>();
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task LoadApiKeysAsync(ISettingsService settingsService)
    {
        try
        {
            await settingsService.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not load the API keys");
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_services is not { } services)
            return;

        // The process can end before a debounced background save runs.
        try
        {
            services.GetRequiredService<SettingsService>().Flush();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not save the settings");
        }

        try
        {
            services.GetRequiredService<IAudioCaptureService>().Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not release the audio device");
        }

        try
        {
            // Releases the global hotkey grabs and their X connection.
            services.GetRequiredService<DesktopWindowService>().Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not release the window services");
        }
    }
}
