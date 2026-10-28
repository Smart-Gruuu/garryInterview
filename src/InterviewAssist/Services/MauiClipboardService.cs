using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

public sealed class MauiClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) => Clipboard.Default.SetTextAsync(text);
}
