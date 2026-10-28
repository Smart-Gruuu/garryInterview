using Avalonia;
using InterviewAssist.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Desktop;

internal static class Program
{
    /// <summary>X11 WM_CLASS; matches StartupWMClass in packaging/linux/interview-assist.desktop.</summary>
    private const string WmClass = "interview-assist";

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DesktopLog.Write(LogLevel.Critical, "Unhandled", "The app crashed", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DesktopLog.Write(LogLevel.Error, "Unobserved", "A background task failed", e.Exception);
            e.SetObserved();
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = WmClass })
            .WithInterFont()
            .LogToTrace();
}
