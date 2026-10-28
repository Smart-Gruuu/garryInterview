using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

/// <summary>Opens links with the platform launcher (xdg-open on Linux, the shell on Windows).</summary>
public sealed class AvaloniaLauncherService : ILauncherService
{
    public Task<bool> OpenUrlAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return MainTopLevel.TryGet()?.Launcher.LaunchUriAsync(uri) ?? Task.FromResult(false);
    }
}
