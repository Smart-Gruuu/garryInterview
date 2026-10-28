using InterviewAssist.Desktop.ViewModels;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

/// <summary>Switches the main window's tab through <see cref="ShellViewModel"/>.</summary>
public sealed class AvaloniaNavigationService(ShellViewModel shell, IUiDispatcher dispatcher) : INavigationService
{
    public Task GoToAsync(AppPage page)
    {
        if (!Enum.IsDefined(page))
            throw new ArgumentOutOfRangeException(nameof(page), page, null);

        return dispatcher.InvokeAsync(() => shell.SelectedTabIndex = (int)page);
    }
}
