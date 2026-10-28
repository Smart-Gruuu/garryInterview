using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

/// <summary>The app data folder settings.json has always lived in, so existing settings carry over.</summary>
public sealed class MauiSettingsLocation : ISettingsLocation
{
    public string DirectoryPath => FileSystem.AppDataDirectory;
}
