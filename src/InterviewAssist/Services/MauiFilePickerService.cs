using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

/// <summary>
/// <see cref="FilePicker"/> filtered to the given extensions. Android's picker filters by MIME type, so the
/// extensions are mapped to their MIME types there.
/// </summary>
public sealed class MauiFilePickerService : IFilePickerService
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
        var mimeTypes = extensions
            .Select(e => s_mimeTypes.GetValueOrDefault(e))
            .OfType<string>()
            .Distinct()
            .ToArray();

        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = extensions,
            [DevicePlatform.Android] = mimeTypes.Length > 0 ? mimeTypes : ["*/*"],
        });

        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = title,
            FileTypes = fileTypes,
        });

        return file is null ? null : new PickedFile(file.FileName, file.OpenReadAsync);
    }
}
