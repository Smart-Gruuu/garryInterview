namespace InterviewAssist.Presentation.Services;

/// <summary>The app's three screens.</summary>
public enum AppPage
{
    Live,
    Setup,
    Settings,
}

/// <summary>Switches the visible screen (tab).</summary>
public interface INavigationService
{
    Task GoToAsync(AppPage page);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Opens web links (e.g. the API key sign-up pages) in the user's browser.</summary>
public interface ILauncherService
{
    /// <summary>Returns false when nothing can open <paramref name="uri"/>. May throw when launching fails.</summary>
    Task<bool> OpenUrlAsync(Uri uri);
}

/// <summary>A file the user picked. <see cref="OpenReadAsync"/> may be called once; dispose the stream.</summary>
public sealed record PickedFile(string FileName, Func<Task<Stream>> OpenReadAsync);

public interface IFilePickerService
{
    /// <summary>
    /// Lets the user pick one document. <paramref name="extensions"/> are lower-case with the dot (".pdf").
    /// Returns null when cancelled. Throws when the picker itself can't be shown.
    /// </summary>
    Task<PickedFile?> PickDocumentAsync(string title, IReadOnlyList<string> extensions);
}

/// <summary>Simple modal dialogs. All methods must be called on the UI thread.</summary>
public interface IDialogService
{
    /// <summary>Returns true when the user chose <paramref name="accept"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel");

    Task AlertAsync(string title, string message, string ok = "OK");

    /// <summary>Lets the user pick one of <paramref name="options"/>; returns null when cancelled.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel");
}

public enum AppPlatform
{
    Windows,
    Android,
    Linux,
    MacOS,
    Other,
}

/// <summary>Facts about the running app that view models show or branch on.</summary>
public interface IAppEnvironment
{
    AppPlatform Platform { get; }

    /// <summary>E.g. "1.0 (build 1)".</summary>
    string VersionText { get; }
}
