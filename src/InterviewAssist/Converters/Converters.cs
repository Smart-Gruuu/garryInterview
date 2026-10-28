using System.Globalization;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Converters;

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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Non-null → true.</summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// <see cref="StatusKind"/> → theme color (status dot). Reads the current app resources, so a binding re-evaluates
/// with the new theme only when its source raises PropertyChanged again.
/// </summary>
public sealed class StatusKindToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (key, fallback) = value switch
        {
            StatusKind.Listening => ("SuccessColor", Color.FromArgb("#3FB950")),
            StatusKind.Connecting or StatusKind.Reconnecting => ("WarningColor", Color.FromArgb("#D29922")),
            StatusKind.Error => ("DangerColor", Color.FromArgb("#F85149")),
            _ => ("SubtleTextColor", Color.FromArgb("#8B8FA3")),
        };

        return ThemeResources.ForTarget(ThemeResources.GetColor(key, fallback), targetType);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true → AccentColor, false → SurfaceAltColor (highlighted vs. normal chips).</summary>
public sealed class BoolToAccentColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is true
            ? ThemeResources.GetColor("AccentColor", Color.FromArgb("#7C83FD"))
            : ThemeResources.GetColor("SurfaceAltColor", Color.FromArgb("#2A2B36"));
        return ThemeResources.ForTarget(color, targetType);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// true → the GridLength in ConverterParameter ("0.3*", "*", "Auto", "120"; default "*"), false → 0.
/// Used to collapse a Grid row.
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ParseGridLength(parameter) : new GridLength(0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    private static GridLength ParseGridLength(object? parameter)
    {
        if (parameter is GridLength length)
        {
            return length;
        }

        var text = (parameter as string)?.Trim();
        if (string.IsNullOrEmpty(text) || text == "*")
        {
            return GridLength.Star;
        }

        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }

        if (text.EndsWith('*'))
        {
            return double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) && weight > 0
                ? new GridLength(weight, GridUnitType.Star)
                : GridLength.Star;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var absolute) && absolute >= 0
            ? new GridLength(absolute)
            : GridLength.Star;
    }
}

internal static class ThemeResources
{
    /// <summary>Looks up a theme color in the application resources (including merged dictionaries).</summary>
    public static Color GetColor(string key, Color fallback)
    {
        if (Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value))
        {
            switch (value)
            {
                case Color color:
                    return color;
                case SolidColorBrush { Color: { } brushColor }:
                    return brushColor;
            }
        }

        return fallback;
    }

    /// <summary>Returns a brush when the target property is a Brush (e.g. Fill/Background), else the color.</summary>
    public static object ForTarget(Color color, Type targetType) =>
        typeof(Brush).IsAssignableFrom(targetType) ? new SolidColorBrush(color) : color;
}
