using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Desktop.Converters;

/// <summary>true → false, anything else → true. Two-way.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Non-blank string → true.</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary><see cref="StatusKind"/> → theme brush (status dot): Success, Warning, Danger or SubtleText.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (key, fallback) = value switch
        {
            StatusKind.Listening => ("SuccessBrush", "#34D399"),
            StatusKind.Connecting or StatusKind.Reconnecting => ("WarningBrush", "#FBBF24"),
            StatusKind.Error => ("DangerBrush", "#E5484D"),
            _ => ("SubtleTextBrush", "#9A9DAB"),
        };

        return ThemeResources.ForTarget(ThemeResources.GetBrush(key, fallback), targetType);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>true → AccentBrush, anything else → SurfaceAltBrush (highlighted vs. normal chips).</summary>
public sealed class BoolToAccentBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = value is true
            ? ThemeResources.GetBrush("AccentBrush", "#818CF8")
            : ThemeResources.GetBrush("SurfaceAltBrush", "#2A2C34");
        return ThemeResources.ForTarget(brush, targetType);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

internal static class ThemeResources
{
    /// <summary>
    /// The application's theme brush for <paramref name="key"/>. AvaloniaThemeService recolours these brushes in
    /// place, so a converted value follows later theme changes without the binding re-evaluating.
    /// </summary>
    public static IBrush GetBrush(string key, string fallbackHex)
    {
        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>The brush, or its color when the target property is a <see cref="Color"/>.</summary>
    public static object ForTarget(IBrush brush, Type targetType) =>
        targetType == typeof(Color) && brush is ISolidColorBrush solid ? solid.Color : brush;
}
