using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace InterviewAssist.Desktop.Services;

/// <summary>The main window, which owns the clipboard, launcher and storage provider the adapters use.</summary>
internal static class MainTopLevel
{
    public static TopLevel? TryGet() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static TopLevel Get() =>
        TryGet() ?? throw new InvalidOperationException("The main window is not open.");
}
