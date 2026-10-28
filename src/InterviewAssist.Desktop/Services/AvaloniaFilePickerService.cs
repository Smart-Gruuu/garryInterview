using Avalonia.Platform.Storage;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

/// <summary>The platform open-file dialog (the XDG desktop portal or GTK on Linux), filtered to the given extensions.</summary>
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    private static readonly Dictionary<string, string> s_mimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
    };

    public async Task<PickedFile?> PickDocumentAsync(string title, IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        var storage = MainTopLevel.Get().StorageProvider;
        if (!storage.CanOpen)
            throw new InvalidOperationException("No file dialog is available on this system.");

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [CreateFileType(extensions), FilePickerFileTypes.All],
        });

        if (files.Count == 0)
            return null;

        var file = files[0];
        return new PickedFile(file.Name, async () =>
        {
            using (file)
                return await file.OpenReadAsync();
        });
    }

    private static FilePickerFileType CreateFileType(IReadOnlyList<string> extensions)
    {
        var names = string.Join(", ", extensions.Select(e => e.TrimStart('.')));

        // GTK matches patterns case-sensitively, so "RESUME.PDF" needs its own pattern.
        return new FilePickerFileType($"Documents ({names})")
        {
            Patterns = [.. extensions.SelectMany(e => new[] { "*" + e.ToLowerInvariant(), "*" + e.ToUpperInvariant() }).Distinct()],
            MimeTypes = [.. extensions.Select(e => s_mimeTypes.GetValueOrDefault(e)).OfType<string>().Distinct()],
        };
    }
}
