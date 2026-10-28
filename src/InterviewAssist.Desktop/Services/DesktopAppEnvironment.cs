using System.Reflection;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

public sealed class DesktopAppEnvironment : IAppEnvironment
{
    public AppPlatform Platform { get; } =
        OperatingSystem.IsLinux() ? AppPlatform.Linux
        : OperatingSystem.IsWindows() ? AppPlatform.Windows
        : OperatingSystem.IsMacOS() ? AppPlatform.MacOS
        : AppPlatform.Other;

    /// <summary>E.g. "1.0.0 (Linux desktop)".</summary>
    public string VersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        var assembly = typeof(DesktopAppEnvironment).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "1.0";

        // Drop the "+<commit>" build metadata the SDK appends.
        var plus = version.IndexOf('+');
        if (plus >= 0)
            version = version[..plus];

        var os = OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "unknown OS";
        return $"{version} ({os} desktop)";
    }
}
