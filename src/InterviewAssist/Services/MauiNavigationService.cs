using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

/// <summary>Switches Shell tabs by their absolute routes (see AppShell.xaml).</summary>
public sealed class MauiNavigationService : INavigationService
{
    public Task GoToAsync(AppPage page)
    {
        var route = page switch
        {
            AppPage.Live => "//live",
            AppPage.Setup => "//setup",
            AppPage.Settings => "//settings",
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
        };

        return Shell.Current?.GoToAsync(route) ?? Task.CompletedTask;
    }
}
