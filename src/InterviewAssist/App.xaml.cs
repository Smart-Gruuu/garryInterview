using InterviewAssist.Core.Abstractions;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Settings;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.UI.Windowing;
using Windows.Graphics;
using PlatformWindowService = InterviewAssist.Native.WindowsWindowService;
#elif ANDROID
using PlatformWindowService = InterviewAssist.Native.AndroidWindowService;
#endif

namespace InterviewAssist;

public partial class App : Application
{
    private const double MinWindowWidth = 340;
    private const double MinWindowHeight = 420;
    private const double DefaultWindowWidth = 460;
    private const double DefaultWindowHeight = 720;

    private readonly ISettingsService _settingsService;
    private readonly PlatformWindowService _windowService;
    private readonly ILogger<App> _logger;

    public App(
        ISettingsService settingsService,
        IThemeService themeService,
        PlatformWindowService windowService,
        ILogger<App> logger)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _windowService = windowService;
        _logger = logger;

        themeService.Initialize();
        _ = LoadApiKeysAsync();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var settings = _settingsService.Settings;
        var window = new Window(new AppShell())
        {
            Title = "Interview Assist",
            Width = ValidSize(settings.WindowWidth, DefaultWindowWidth, MinWindowWidth),
            Height = ValidSize(settings.WindowHeight, DefaultWindowHeight, MinWindowHeight),
            MinimumWidth = MinWindowWidth,
            MinimumHeight = MinWindowHeight,
        };

        if (double.IsFinite(settings.WindowX) && double.IsFinite(settings.WindowY))
        {
            window.X = settings.WindowX;
            window.Y = settings.WindowY;
        }

        window.HandlerChanged += OnWindowHandlerChanged;
#if WINDOWS
        window.SizeChanged += OnWindowSizeChanged;
        window.Destroying += OnWindowDestroying;
#endif
        return window;
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        _ = _settingsService.SaveAsync();
    }

    private static double ValidSize(double value, double fallback, double minimum) =>
        double.IsFinite(value) && value > 0 ? Math.Max(value, minimum) : fallback;

    private async Task LoadApiKeysAsync()
    {
        try
        {
            await _settingsService.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the API keys");
        }
    }

    private void OnWindowHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is not Window window || window.Handler is null)
            return;

        // Attach once per window.
        window.HandlerChanged -= OnWindowHandlerChanged;

#if WINDOWS
        EnsureOnScreen(window);
#endif
        try
        {
            _windowService.Attach(window);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not attach the native window behaviour");
        }
    }

#if WINDOWS
    private void OnWindowSizeChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
            SaveWindowBounds(window);
    }

    private void OnWindowDestroying(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.SizeChanged -= OnWindowSizeChanged;
        window.Destroying -= OnWindowDestroying;
        SaveWindowBounds(window);

        // The process can exit before a debounced background save gets to run.
        (_settingsService as SettingsService)?.Flush();
    }

    private void SaveWindowBounds(Window window)
    {
        // Minimized windows report a far-off position and maximized ones the screen size: keep the last normal bounds.
        if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window
            {
                AppWindow.Presenter: OverlappedPresenter { State: not OverlappedPresenterState.Restored },
            })
        {
            return;
        }

        double x = window.X, y = window.Y, width = window.Width, height = window.Height;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
        {
            return;
        }

        var settings = _settingsService.Settings;
        settings.WindowX = x;
        settings.WindowY = y;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
    }

    // A window restored onto a monitor that has since been unplugged would be unreachable (in stealth mode it
    // is not even in the taskbar), so the middle of its title bar must be on some display.
    private void EnsureOnScreen(Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
                return;

            var appWindow = nativeWindow.AppWindow;
            var position = appWindow.Position;
            var size = appWindow.Size;

            var titleBarPoint = new PointInt32(position.X + size.Width / 2, position.Y + 16);
            if (DisplayArea.GetFromPoint(titleBarPoint, DisplayAreaFallback.None) is not null)
                return;

            var work = DisplayArea.Primary.WorkArea;
            var width = Math.Min(size.Width, work.Width);
            var height = Math.Min(size.Height, work.Height);
            appWindow.MoveAndResize(new RectInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2,
                width,
                height));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the window position");
        }
    }
#endif
}
