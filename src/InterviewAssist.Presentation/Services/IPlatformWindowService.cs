namespace InterviewAssist.Presentation.Services;

public enum WindowCommand
{
    /// <summary>Answer the question just asked (Enter in the window, or the global Ask hotkey).</summary>
    Ask,

    /// <summary>Stop the answer being generated (Escape in the window).</summary>
    Cancel,
}

/// <summary>
/// Native window behaviour: always-on-top, opacity, stealth (screen-capture exclusion), keyboard shortcuts and
/// Ctrl + mouse wheel font sizing. Each UI host implements it for its window type and attaches it to the main window
/// itself (the attach method is host-specific, so it is not part of this interface). Implementations keep applying
/// Settings.AlwaysOnTop / Opacity / StealthMode / AskHotkey / ToggleWindowHotkey whenever they change.
/// </summary>
public interface IPlatformWindowService
{
    bool SupportsAlwaysOnTop { get; }
    bool SupportsOpacity { get; }
    bool SupportsStealth { get; }
    bool SupportsGlobalHotkeys { get; }

    /// <summary>
    /// When true, Enter / Escape pressed in the window while keyboard focus is NOT in a text input raise
    /// <see cref="CommandReceived"/> (Ask / Cancel) and are marked handled so focused buttons don't also react.
    /// The live page turns this on while it is visible.
    /// </summary>
    bool InterceptEnterKey { get; set; }

    /// <summary>Shows the window if hidden (and brings it to front), hides it otherwise.</summary>
    void ToggleVisibility();

    /// <summary>Raised on the UI thread.</summary>
    event EventHandler<WindowCommand>? CommandReceived;

    /// <summary>Non-fatal problem worth showing (e.g. "Hotkey Ctrl+Shift+H is already used by another app"). UI thread.</summary>
    event EventHandler<string>? Warning;
}

/// <summary>Applies the color theme and font size to the host UI framework's resources.</summary>
public interface IThemeService
{
    IReadOnlyList<string> ThemeNames { get; }

    /// <summary>Applies Settings.Theme and Settings.FontSize now and re-applies whenever they change. Call once at startup.</summary>
    void Initialize();
}
