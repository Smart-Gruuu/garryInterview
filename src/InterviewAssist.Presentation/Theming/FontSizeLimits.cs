namespace InterviewAssist.Presentation.Theming;

/// <summary>The range of Settings.FontSize (the base text size; other sizes are derived from it).</summary>
public static class FontSizeLimits
{
    public const double Min = 11;
    public const double Max = 22;
    public const double Default = 14;

    /// <summary><paramref name="fontSize"/> clamped to the range; <see cref="Default"/> when it isn't a number.</summary>
    public static double Clamp(double fontSize) =>
        double.IsFinite(fontSize) ? Math.Clamp(fontSize, Min, Max) : Default;
}
