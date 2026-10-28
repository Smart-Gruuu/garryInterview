using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

public sealed class MauiLauncherService : ILauncherService
{
    public Task<bool> OpenUrlAsync(Uri uri) => Launcher.Default.OpenAsync(uri);
}
