using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

/// <summary>
/// Linux: $XDG_CONFIG_HOME/interview-assist (default ~/.config/interview-assist); Windows: %APPDATA%\InterviewAssist.Desktop
/// (kept apart from the MAUI app's folder); macOS: ~/Library/Application Support/InterviewAssist.
/// </summary>
public sealed class DesktopSettingsLocation : ISettingsLocation
{
    public string DirectoryPath { get; } = Resolve();

    private static string Resolve()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            // GetFolderPath ignores %APPDATA%; honour it like XDG_CONFIG_HOME so a run can be pointed at another folder.
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(appData) || !Path.IsPathRooted(appData))
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return Path.Combine(appData, "InterviewAssist.Desktop");
        }

        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "InterviewAssist");

        // The XDG spec says a relative XDG_CONFIG_HOME is invalid and must be ignored.
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
            configHome = Path.Combine(home, ".config");

        return Path.Combine(configHome, "interview-assist");
    }
}
