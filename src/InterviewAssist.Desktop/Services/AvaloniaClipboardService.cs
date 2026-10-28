using Avalonia.Input.Platform;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        var clipboard = MainTopLevel.Get().Clipboard
            ?? throw new InvalidOperationException("The clipboard is not available.");
        return clipboard.SetTextAsync(text);
    }
}
