using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

/// <summary>Alerts and action sheets shown over the current Shell page.</summary>
public sealed class MauiDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel") =>
        CurrentPage is { } page
            ? page.DisplayAlert(title, message, accept, cancel)
            : Task.FromResult(false);

    public Task AlertAsync(string title, string message, string ok = "OK") =>
        CurrentPage?.DisplayAlert(title, message, ok) ?? Task.CompletedTask;

    public async Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel")
    {
        if (CurrentPage is not { } page)
            return null;

        var choice = await page.DisplayActionSheet(title, cancel, null, [.. options]);
        return choice is not null && options.Contains(choice) ? choice : null;
    }

    private static Page? CurrentPage =>
        Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;
}
