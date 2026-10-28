namespace InterviewAssist.Presentation.Theming;

/// <summary>
/// The colors of one theme as "#RRGGBB" strings. Positional, so a palette cannot compile without a value for every
/// theme key.
/// </summary>
public sealed record ThemePalette(
    bool IsDark,
    string AppBackground,
    string Surface,
    string SurfaceAlt,
    string Border,
    string Text,
    string SubtleText,
    string Accent,
    string OnAccent,
    string InterimText,
    string QuestionText,
    string Success,
    string Warning,
    string Danger,
    string CodeBackground,
    string CodeText);

/// <summary>The color themes (Settings.Theme). Each UI host turns a palette into its own color resources.</summary>
public static class ThemePalettes
{
    public const string DefaultName = "Dark";

    // Neutral charcoal, soft indigo accent. The MAUI Colors.xaml and the Avalonia App.axaml repeat these values.
    public static ThemePalette Dark { get; } = new(
        IsDark: true,
        AppBackground: "#16171B", Surface: "#1F2026", SurfaceAlt: "#2A2C34", Border: "#363842",
        Text: "#ECEDF1", SubtleText: "#9A9DAB",
        Accent: "#818CF8", OnAccent: "#10111A",
        InterimText: "#A5B4FC", QuestionText: "#C4B5FD",
        Success: "#34D399", Warning: "#FBBF24", Danger: "#E5484D",
        CodeBackground: "#111216", CodeText: "#DCDFF5");

    private static readonly (string Name, ThemePalette Palette)[] s_themes =
    [
        (DefaultName, Dark),

        ("Light", new(
            IsDark: false,
            AppBackground: "#F3F4F7", Surface: "#FFFFFF", SurfaceAlt: "#ECEEF3", Border: "#D6D9E1",
            Text: "#15171C", SubtleText: "#5D6371",
            Accent: "#4F46E5", OnAccent: "#FFFFFF",
            InterimText: "#2563EB", QuestionText: "#6D28D9",
            Success: "#15803D", Warning: "#B45309", Danger: "#DC2626",
            CodeBackground: "#EEF0F5", CodeText: "#1E293B")),

        // Deep navy, cyan accent.
        ("Midnight", new(
            IsDark: true,
            AppBackground: "#0B1220", Surface: "#111A2C", SurfaceAlt: "#1A2540", Border: "#28365A",
            Text: "#E6EDF7", SubtleText: "#8EA0BF",
            Accent: "#22D3EE", OnAccent: "#05232B",
            InterimText: "#7DD3FC", QuestionText: "#FCD68A",
            Success: "#34D399", Warning: "#FBBF24", Danger: "#EF4444",
            CodeBackground: "#070C17", CodeText: "#BAE6FD")),

        // Arctic Ice Studio's Nord palette (polar night, snow storm, frost, aurora).
        ("Nord", new(
            IsDark: true,
            AppBackground: "#2E3440", Surface: "#3B4252", SurfaceAlt: "#434C5E", Border: "#4C566A",
            Text: "#ECEFF4", SubtleText: "#AAB4C4",
            Accent: "#88C0D0", OnAccent: "#2E3440",
            InterimText: "#8FBCBB", QuestionText: "#EBCB8B",
            Success: "#A3BE8C", Warning: "#D08770", Danger: "#E06C75",
            CodeBackground: "#262B35", CodeText: "#D8DEE9")),

        // Near-black with phosphor-green text.
        ("Terminal", new(
            IsDark: true,
            AppBackground: "#0A0D0A", Surface: "#101410", SurfaceAlt: "#182018", Border: "#253025",
            Text: "#9EF0AA", SubtleText: "#5F9A69",
            Accent: "#3DDC5A", OnAccent: "#03140A",
            InterimText: "#7FE3D2", QuestionText: "#FFD866",
            Success: "#4ADE80", Warning: "#FACC15", Danger: "#F0524F",
            CodeBackground: "#050805", CodeText: "#C8FACC")),
    ];

    private static readonly Dictionary<string, ThemePalette> s_byName =
        s_themes.ToDictionary(t => t.Name, t => t.Palette, StringComparer.OrdinalIgnoreCase);

    /// <summary>Theme names in display order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. s_themes.Select(t => t.Name)];

    /// <summary>The palette named <paramref name="name"/> (case-insensitive); <see cref="Dark"/> for an unknown name.</summary>
    public static ThemePalette Get(string? name) =>
        name is not null && s_byName.TryGetValue(name, out var palette) ? palette : Dark;

    /// <summary>Every theme color resource key ("AppBackgroundColor" … "CodeTextColor") with its hex value.</summary>
    public static IReadOnlyList<(string Key, string Hex)> ColorResources(ThemePalette p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return
        [
            ("AppBackgroundColor", p.AppBackground),
            ("SurfaceColor", p.Surface),
            ("SurfaceAltColor", p.SurfaceAlt),
            ("BorderColor", p.Border),
            ("TextColor", p.Text),
            ("SubtleTextColor", p.SubtleText),
            ("AccentColor", p.Accent),
            ("OnAccentColor", p.OnAccent),
            ("InterimTextColor", p.InterimText),
            ("QuestionTextColor", p.QuestionText),
            ("SuccessColor", p.Success),
            ("WarningColor", p.Warning),
            ("DangerColor", p.Danger),
            ("CodeBackgroundColor", p.CodeBackground),
            ("CodeTextColor", p.CodeText),
        ];
    }
}
