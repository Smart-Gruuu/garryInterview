using System.ComponentModel;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Theming;

namespace InterviewAssist.Services;

/// <summary>
/// Applies Settings.Theme and Settings.FontSize by writing the theme keys (see Resources/Styles/Colors.xaml)
/// into Application.Resources; every view reads them through {DynamicResource}.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly AppSettings _settings;
    private ThemePalette? _appliedPalette;
    private double _appliedFontSize = double.NaN;
    private bool _initialized;

    public ThemeService(ISettingsService settingsService)
    {
        _settings = settingsService.Settings;
    }

    public IReadOnlyList<string> ThemeNames => ThemePalettes.Names;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        Apply();
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.Theme) or nameof(AppSettings.FontSize) or null or ""))
            return;

        if (MainThread.IsMainThread)
            Apply();
        else
            MainThread.BeginInvokeOnMainThread(Apply);
    }

    private void Apply()
    {
        var app = Application.Current;
        if (app is null)
            return;

        var resources = app.Resources;

        var palette = ThemePalettes.Get(_settings.Theme);
        if (!ReferenceEquals(palette, _appliedPalette))
        {
            ApplyPalette(resources, palette);

            // Native parts (title bar buttons, drop-downs, scroll bars, text selection) follow the app theme.
            app.UserAppTheme = palette.IsDark ? AppTheme.Dark : AppTheme.Light;
            _appliedPalette = palette;
        }

        var fontSize = FontSizeLimits.Clamp(_settings.FontSize);
        if (fontSize != _appliedFontSize)
        {
            resources["BaseFontSize"] = fontSize;
            resources["SmallFontSize"] = fontSize - 2;
            resources["LargeFontSize"] = fontSize + 3;
            resources["AnswerFontSize"] = fontSize + 1;
            _appliedFontSize = fontSize;
        }
    }

    private static void ApplyPalette(ResourceDictionary resources, ThemePalette palette)
    {
        foreach (var (key, hex) in ThemePalettes.ColorResources(palette))
            resources[key] = Color.FromArgb(hex);
    }
}
