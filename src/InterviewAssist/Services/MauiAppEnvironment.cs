using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

public sealed class MauiAppEnvironment : IAppEnvironment
{
    public AppPlatform Platform
    {
        get
        {
            var platform = DeviceInfo.Current.Platform;
            if (platform == DevicePlatform.WinUI)
                return AppPlatform.Windows;
            if (platform == DevicePlatform.Android)
                return AppPlatform.Android;
            if (platform == DevicePlatform.MacCatalyst || platform == DevicePlatform.macOS)
                return AppPlatform.MacOS;
            return AppPlatform.Other;
        }
    }

    public string VersionText => $"{AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";
}
