using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Theming;

namespace InterviewAssist.Desktop.Services;

/// <summary>
/// Applies Settings.Theme and Settings.FontSize to Application.Resources: "{Key}Color" and "{Key}Brush" for every
/// palette key, the font sizes, and the Fluent control keys that should follow the palette (inputs, buttons, tabs).
/// Views read them with {DynamicResource}.
/// </summary>
/// <remarks>
/// Brushes are recoloured in place rather than replaced, so a brush handed out earlier (e.g. by a converter) follows
/// theme changes too.
/// </remarks>
public sealed class AvaloniaThemeService : IThemeService
{
    // Fluent resource keys pointed at the palette, so standard controls look like the MAUI app's.
    private static readonly (string Key, Func<PaletteColors, Color> Color)[] s_fluentBrushes =
    [
        ("ButtonBackground", c => c.SurfaceAlt),
        ("ButtonBackgroundPointerOver", c => c.Hover(c.SurfaceAlt)),
        ("ButtonBackgroundPressed", c => c.Pressed(c.SurfaceAlt)),
        ("ButtonBackgroundDisabled", c => c.Disabled(c.SurfaceAlt)),
        ("ButtonForeground", c => c.Text),
        ("ButtonForegroundPointerOver", c => c.Text),
        ("ButtonForegroundPressed", c => c.Text),
        ("ButtonForegroundDisabled", c => c.SubtleText),

        ("TextControlBackground", c => c.SurfaceAlt),
        ("TextControlBackgroundPointerOver", c => c.SurfaceAlt),
        ("TextControlBackgroundFocused", c => c.SurfaceAlt),
        ("TextControlBackgroundDisabled", c => c.Disabled(c.SurfaceAlt)),
        ("TextControlForeground", c => c.Text),
        ("TextControlForegroundPointerOver", c => c.Text),
        ("TextControlForegroundFocused", c => c.Text),
        ("TextControlForegroundDisabled", c => c.SubtleText),
        ("TextControlPlaceholderForeground", c => c.SubtleText),
        ("TextControlPlaceholderForegroundPointerOver", c => c.SubtleText),
        ("TextControlPlaceholderForegroundFocused", c => c.SubtleText),
        ("TextControlPlaceholderForegroundDisabled", c => c.SubtleText),
        ("TextControlBorderBrush", c => c.Border),
        ("TextControlBorderBrushPointerOver", c => c.Hover(c.Border)),
        ("TextControlBorderBrushFocused", c => c.Accent),
        ("TextControlBorderBrushDisabled", c => c.Border),
        ("TextControlSelectionHighlightColor", c => c.Accent),

        ("ComboBoxBackground", c => c.SurfaceAlt),
        ("ComboBoxBackgroundPointerOver", c => c.Hover(c.SurfaceAlt)),
        ("ComboBoxBackgroundPressed", c => c.Pressed(c.SurfaceAlt)),
        ("ComboBoxBackgroundDisabled", c => c.Disabled(c.SurfaceAlt)),
        ("ComboBoxBackgroundUnfocused", c => c.SurfaceAlt),
        ("ComboBoxBorderBrush", c => c.Border),
        ("ComboBoxBorderBrushPointerOver", c => c.Hover(c.Border)),
        ("ComboBoxBorderBrushPressed", c => c.Border),
        ("ComboBoxBorderBrushDisabled", c => c.Border),
        ("ComboBoxForeground", c => c.Text),
        ("ComboBoxForegroundFocused", c => c.Text),
        ("ComboBoxForegroundFocusedPressed", c => c.Text),
        ("ComboBoxForegroundDisabled", c => c.SubtleText),
        ("ComboBoxDropDownGlyphForeground", c => c.SubtleText),
        ("ComboBoxDropDownBackground", c => c.Surface),
        ("ComboBoxDropDownBorderBrush", c => c.Border),
        ("ComboBoxItemForeground", c => c.Text),
        ("ComboBoxItemForegroundPointerOver", c => c.Text),
        ("ComboBoxItemForegroundPressed", c => c.Text),
        ("ComboBoxItemForegroundSelected", c => c.Text),
        ("ComboBoxItemForegroundSelectedPointerOver", c => c.Text),
        ("ComboBoxItemForegroundSelectedPressed", c => c.Text),
        ("ComboBoxItemBackgroundPointerOver", c => c.SurfaceAlt),
        ("ComboBoxItemBackgroundPressed", c => c.Pressed(c.Surface)),
        ("ComboBoxItemBackgroundSelected", c => c.Selected),
        ("ComboBoxItemBackgroundSelectedPointerOver", c => c.Hover(c.Selected)),
        ("ComboBoxItemBackgroundSelectedPressed", c => c.Pressed(c.Selected)),

        ("TabItemHeaderForegroundSelected", c => c.Text),
        ("TabItemHeaderForegroundSelectedPointerOver", c => c.Text),
        ("TabItemHeaderForegroundSelectedPressed", c => c.Text),
        ("TabItemHeaderForegroundUnselected", c => c.SubtleText),
        ("TabItemHeaderForegroundUnselectedPointerOver", c => c.Text),
        ("TabItemHeaderForegroundUnselectedPressed", c => c.Text),
        ("TabItemHeaderSelectedPipeFill", c => c.Accent),

        ("ToolTipBackground", c => c.SurfaceAlt),
        ("ToolTipForeground", c => c.Text),
        ("ToolTipBorderBrush", c => c.Border),
    ];

    private readonly AppSettings _settings;
    private readonly IUiDispatcher _dispatcher;
    private ThemePalette? _appliedPalette;
    private double _appliedFontSize = double.NaN;
    private bool _initialized;

    public AvaloniaThemeService(ISettingsService settingsService, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settings = settingsService.Settings;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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

        if (_dispatcher.IsUiThread)
            Apply();
        else
            _dispatcher.Post(Apply);
    }

    private void Apply()
    {
        if (Application.Current is not { } app)
            return;

        var resources = app.Resources;

        var palette = ThemePalettes.Get(_settings.Theme);
        if (!ReferenceEquals(palette, _appliedPalette))
        {
            ApplyPalette(app, resources, palette);
            _appliedPalette = palette;
        }

        var fontSize = FontSizeLimits.Clamp(_settings.FontSize);
        if (fontSize != _appliedFontSize)
        {
            resources["BaseFontSize"] = fontSize;
            resources["SmallFontSize"] = fontSize - 2;
            resources["LargeFontSize"] = fontSize + 3;
            resources["AnswerFontSize"] = fontSize + 1;

            // Fluent's inputs, buttons and drop-downs size their text from this.
            resources["ControlContentThemeFontSize"] = fontSize;
            _appliedFontSize = fontSize;
        }
    }

    private static void ApplyPalette(Application app, IResourceDictionary resources, ThemePalette palette)
    {
        foreach (var (key, hex) in ThemePalettes.ColorResources(palette))
        {
            var color = Color.Parse(hex);
            resources[key] = color;
            SetBrush(resources, key[..^"Color".Length] + "Brush", color);
        }

        var colors = new PaletteColors(palette);
        foreach (var (key, color) in s_fluentBrushes)
            SetBrush(resources, key, color(colors));

        // Native-looking parts (scroll bars, toggles, focus rings) follow the theme's light/dark variant and accent.
        var variant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.RequestedThemeVariant = variant;

        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            if (!fluent.Palettes.TryGetValue(variant, out var fluentPalette))
            {
                fluentPalette = new ColorPaletteResources();
                fluent.Palettes[variant] = fluentPalette;
            }

            fluentPalette.Accent = colors.Accent;
            fluentPalette.RegionColor = colors.AppBackground;
            fluentPalette.ErrorText = colors.Danger;
        }
    }

    private static void SetBrush(IResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
            brush.Color = color;
        else
            resources[key] = new SolidColorBrush(color);
    }

    /// <summary>The palette parsed into colors, plus the derived interaction shades.</summary>
    private sealed class PaletteColors(ThemePalette p)
    {
        public Color AppBackground { get; } = Color.Parse(p.AppBackground);
        public Color Surface { get; } = Color.Parse(p.Surface);
        public Color SurfaceAlt { get; } = Color.Parse(p.SurfaceAlt);
        public Color Border { get; } = Color.Parse(p.Border);
        public Color Text { get; } = Color.Parse(p.Text);
        public Color SubtleText { get; } = Color.Parse(p.SubtleText);
        public Color Accent { get; } = Color.Parse(p.Accent);
        public Color Danger { get; } = Color.Parse(p.Danger);

        /// <summary>Highlighted (selected) list row.</summary>
        public Color Selected => Mix(Surface, Accent, 0.28);

        public Color Hover(Color c) => Mix(c, Text, 0.08);

        public Color Pressed(Color c) => Mix(c, Text, 0.14);

        public Color Disabled(Color c) => Mix(c, AppBackground, 0.5);

        private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
