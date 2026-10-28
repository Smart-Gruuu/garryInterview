using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using InterviewAssist.Core.Models;
using InterviewAssist.Desktop.Platform;
using InterviewAssist.Desktop.Services;
using InterviewAssist.Desktop.ViewModels;
using InterviewAssist.Desktop.Views;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Settings;
using InterviewAssist.Presentation.ViewModels;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Desktop;

/// <summary>
/// The app window: the three tabs, bounds restored from and saved to settings, and the native window behaviour
/// (<see cref="DesktopWindowService"/>) attached once it is open.
/// </summary>
/// <remarks>
/// Settings.WindowX/WindowY are the window position in physical screen pixels (Avalonia's <see cref="Window.Position"/>);
/// WindowWidth/WindowHeight are the client size in device-independent pixels.
/// </remarks>
public partial class MainWindow : Window
{
    private const double MinWindowWidth = 340;
    private const double MinWindowHeight = 420;
    private const double DefaultWindowWidth = 460;
    private const double DefaultWindowHeight = 720;

    // Null only in the XAML previewer's instance.
    private readonly ShellViewModel? _shell;
    private readonly MainViewModel? _liveViewModel;
    private readonly SettingsService? _settingsService;
    private readonly DesktopWindowService? _windowService;
    private readonly ILogger<MainWindow>? _logger;

    private bool _isOpen;
    private bool _liveTabShown;
    private PixelPoint? _normalPosition;
    private Size? _normalSize;

    /// <summary>For the XAML previewer.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(
        ShellViewModel shell,
        LiveView liveView,
        SetupView setupView,
        SettingsView settingsView,
        MainViewModel liveViewModel,
        SettingsService settingsService,
        DesktopWindowService windowService,
        AvaloniaDialogService dialogs,
        ILogger<MainWindow> logger)
        : this()
    {
        _shell = shell;
        _liveViewModel = liveViewModel;
        _settingsService = settingsService;
        _windowService = windowService;
        _logger = logger;

        DataContext = shell;
        LiveTab.Content = liveView;
        SetupTab.Content = setupView;
        SettingsTab.Content = settingsView;

        // Registered before the window service's key handler (attached on open), so an open dialog sees Enter/Escape first.
        dialogs.Attach(this, DialogLayer);

        RestoreBounds(settingsService.Settings);
        Icon = CreateIcon();

        PositionChanged += (_, _) => RememberNormalBounds();
        shell.PropertyChanged += OnShellPropertyChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        try
        {
            _windowService?.Attach(this);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not attach the native window behaviour");
        }

        _isOpen = true;
        RememberNormalBounds();
        UpdateLiveTabShown();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;

        _isOpen = false;
        UpdateLiveTabShown();
        SaveBounds();

        // The process can end before the debounced background save runs.
        try
        {
            _settingsService?.Flush();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not save the settings");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ClientSizeProperty || change.Property == WindowStateProperty)
            RememberNormalBounds();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.SelectedTabIndex) or null or "")
            UpdateLiveTabShown();
    }

    /// <summary>The live view model routes Enter/Esc to Ask/Cancel only while its tab is on screen.</summary>
    private void UpdateLiveTabShown()
    {
        if (_shell is null || _liveViewModel is null)
            return;

        var shown = _isOpen && _shell.SelectedPage == AppPage.Live;
        if (shown == _liveTabShown)
            return;

        _liveTabShown = shown;
        if (shown)
            _liveViewModel.OnPageAppearing();
        else
            _liveViewModel.OnPageDisappearing();
    }

    private void RestoreBounds(AppSettings settings)
    {
        var width = ValidSize(settings.WindowWidth, DefaultWindowWidth, MinWindowWidth);
        var height = ValidSize(settings.WindowHeight, DefaultWindowHeight, MinWindowHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Avalonia.Platform.Screen? screen = null;
        try
        {
            if (double.IsFinite(settings.WindowX) && double.IsFinite(settings.WindowY)
                && Math.Abs(settings.WindowX) < int.MaxValue && Math.Abs(settings.WindowY) < int.MaxValue)
            {
                var position = new PixelPoint((int)Math.Round(settings.WindowX), (int)Math.Round(settings.WindowY));

                // A window restored onto a display that has since been unplugged would be unreachable, so part of
                // its title bar must be on some display.
                screen = Screens.ScreenFromPoint(position + new PixelVector(48, 12));
                if (screen is not null)
                {
                    Position = position;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }

            screen ??= Screens.Primary;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not check the saved window position");
        }

        if (screen is not null && screen.Scaling > 0)
        {
            width = Math.Min(width, Math.Max(MinWindowWidth, screen.WorkingArea.Width / screen.Scaling));
            height = Math.Min(height, Math.Max(MinWindowHeight, screen.WorkingArea.Height / screen.Scaling));
        }

        Width = width;
        Height = height;
    }

    // Maximized, minimized and hidden windows don't report useful bounds: keep the last normal ones.
    private void RememberNormalBounds()
    {
        if (!_isOpen || !IsVisible || WindowState != WindowState.Normal)
            return;

        _normalPosition = Position;
        if (ClientSize is { Width: > 0, Height: > 0 } size)
            _normalSize = size;
    }

    private void SaveBounds()
    {
        if (_settingsService is null)
            return;

        var settings = _settingsService.Settings;
        if (_normalPosition is { } position)
        {
            settings.WindowX = position.X;
            settings.WindowY = position.Y;
        }

        if (_normalSize is { } size)
        {
            settings.WindowWidth = Math.Max(size.Width, MinWindowWidth);
            settings.WindowHeight = Math.Max(size.Height, MinWindowHeight);
        }
    }

    private static double ValidSize(double value, double fallback, double minimum) =>
        double.IsFinite(value) && value > 0 ? Math.Max(value, minimum) : fallback;

    /// <summary>
    /// The app icon (src/InterviewAssist/Resources/AppIcon: appicon.svg + appiconfg.svg, as in
    /// packaging/linux/interview-assist.png), drawn at runtime so the project needs no embedded image.
    /// </summary>
    private WindowIcon? CreateIcon()
    {
        const int size = 64;
        const double viewBox = 456;

        try
        {
            var background = new ImmutableSolidColorBrush(Color.Parse("#1E1B2E"));
            var foreground = new ImmutableSolidColorBrush(Color.Parse("#818CF8"));
            var bubble = Geometry.Parse(
                "M 156 128 H 300 A 36 36 0 0 1 336 164 V 256 A 36 36 0 0 1 300 292 H 214 L 166 334 L 172 292 H 156 "
                + "A 36 36 0 0 1 120 256 V 164 A 36 36 0 0 1 156 128 Z");
            (double X, double Y, double Height)[] bars = [(159, 192, 36), (189, 174, 72), (219, 160, 100), (249, 174, 72), (279, 192, 36)];

            var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateScale(size / viewBox, size / viewBox)))
            {
                context.DrawRectangle(background, null, new RoundedRect(new Rect(0, 0, viewBox, viewBox), 96));
                context.DrawGeometry(foreground, null, bubble);
                foreach (var (x, y, height) in bars)
                    context.DrawRectangle(background, null, new RoundedRect(new Rect(x, y, 18, height), 9));
            }

            return new WindowIcon(bitmap);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not draw the window icon");
            return null;
        }
    }
}
